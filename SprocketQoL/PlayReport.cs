using BepInEx.Logging;
using HarmonyLib;
using Sprocket.VehicleDesigner;

namespace SprocketQoL;

/// Log lines around Play, flushed to disk: a crash inside the game's own code writes nothing, so a log that ends after
/// "QOL_PLAY building" and before "QOL_PLAY built" shows the crash came while the vehicle was being built, and what it was.
[HarmonyPatch]
internal static class PlayReport
{
    [HarmonyPrefix, HarmonyPatch(typeof(VehicleDesignerCore), nameof(VehicleDesignerCore.BuildDesign))]
    static void Before() { Ui.Guard("Play report", () => DesignEditor.Instance?.ReportForPlay()); Flush(); }

    [HarmonyFinalizer, HarmonyPatch(typeof(VehicleDesignerCore), nameof(VehicleDesignerCore.BuildDesign))]
    static Exception? After(Exception? __exception)
    {
        Plugin.ModLog.LogInfo(__exception == null ? "QOL_PLAY built: the vehicle is ready" : $"QOL_PLAY build failed: {__exception.GetType().Name}: {__exception.Message}");
        Flush();
        return __exception;
    }

    /// BepInEx writes its log file in batches; a hard crash loses the last batch.
    internal static void Flush()
    {
        try { foreach (var l in Logger.Listeners) if (l is DiskLogListener disk) disk.LogWriter?.Flush(); }
        catch (Exception) { } // only a log: never stop Play over it
    }
}
