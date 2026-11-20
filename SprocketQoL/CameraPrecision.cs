using HarmonyLib;
using Sprocket;
using UnityEngine;

namespace SprocketQoL;

/// Native sensitivity helpers are inlined, so reduce the actual orbital fields
/// only while its input step runs. Every field is restored before returning; the
/// game's saved camera preferences and driving camera are never rewritten.
[HarmonyPatch(typeof(OrbitalMovementController), nameof(OrbitalMovementController.ProcessInputs))]
internal static class CameraPrecision
{
    sealed class Saved
    {
        internal Vector2 Look, Pan;
        internal float Zoom;
        internal bool Active;
    }
    static bool unavailable;

    [HarmonyPrefix]
    static void Begin(OrbitalMovementController __instance, out Saved? __state)
    {
        __state = null;
        var editor = DesignEditor.Instance;
        float percent = Plugin.DesignerCameraPercent?.Value ?? 100;
        if (unavailable || editor?.IsReady != true || editor.Core?.HasEditor != true
            || editor.Core.editorState != Sprocket.VehicleDesigner.VehicleDesignerCore.EditorState.Running
            || CameraPrecisionMath.Scale(1, percent, true) == 1) return;
        try
        {
            __state = new Saved { Look = __instance.lookSensitivity, Pan = __instance.panSensitivity, Zoom = __instance.zoomSensitivity, Active = true };
            __instance.lookSensitivity = new(CameraPrecisionMath.Scale(__state.Look.x, percent, true), CameraPrecisionMath.Scale(__state.Look.y, percent, true));
            __instance.panSensitivity = new(CameraPrecisionMath.Scale(__state.Pan.x, percent, true), CameraPrecisionMath.Scale(__state.Pan.y, percent, true));
            __instance.zoomSensitivity = CameraPrecisionMath.Scale(__state.Zoom, percent, true);
        }
        catch (Exception ex)
        {
            Restore(__instance, __state);
            unavailable = true;
            Plugin.ModLog.LogWarning("Designer camera sensitivity is off for this session; the game's controls are unchanged. " + ex.Message);
        }
    }

    [HarmonyPostfix]
    static void End(OrbitalMovementController __instance, Saved? __state) => Restore(__instance, __state);

    [HarmonyFinalizer]
    static Exception? Finish(OrbitalMovementController __instance, Saved? __state, Exception? __exception)
    {
        Restore(__instance, __state);
        return __exception;
    }

    static void Restore(OrbitalMovementController controller, Saved? saved)
    {
        if (saved?.Active != true) return;
        saved.Active = false;
        // Separate guards ensure a failed field cannot leave the other two scaled.
        Ui.Guard("Camera look restore", () => controller.lookSensitivity = saved.Look);
        Ui.Guard("Camera pan restore", () => controller.panSensitivity = saved.Pan);
        Ui.Guard("Camera zoom restore", () => controller.zoomSensitivity = saved.Zoom);
    }
}
