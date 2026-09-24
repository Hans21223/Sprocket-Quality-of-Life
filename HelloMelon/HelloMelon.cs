using HarmonyLib;
using MelonLoader;

[assembly: MelonInfo(typeof(HelloMelon.Mod), "Hello Melon", "1.0.0", "Sprocket Mod Kit", null)]
[assembly: MelonGame(null, null)] // any game

namespace HelloMelon;

/// The smallest check that MelonLoader-style mods run on this Sprocket (through MLLoader on BepInEx). It logs:
///   HELLO_MELON loaded                  - MelonLoader found and started the mod
///   HELLO_MELON scene ...               - MelonLoader's scene events arrive
///   HELLO_MELON found the vehicle editor - game code works under MelonLoader's Il2Cpp* names
///   HELLO_MELON Harmony patch ran       - [HarmonyPatch] in a MelonLoader mod gets applied
public sealed class Mod : MelonMod
{
    internal static MelonLogger.Instance Log = null!;
    bool sawEditor;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        Log.Msg("HELLO_MELON loaded");
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName) => Log.Msg($"HELLO_MELON scene {buildIndex} '{sceneName}'");

    public override void OnUpdate()
    {
        if (sawEditor || UnityEngine.Time.frameCount % 60 != 0) return; // look about once a second
        var core = UnityEngine.Object.FindObjectOfType<Il2CppSprocket.VehicleDesigner.VehicleDesignerCore>();
        if (core == null) return; // (MLLoader's Il2Cpp* copy of this class has no HasEditor; finding it is the test)
        sawEditor = true;
        Log.Msg("HELLO_MELON found the vehicle editor through Il2CppSprocket.VehicleDesigner.VehicleDesignerCore");
    }
}

[HarmonyPatch(typeof(Il2CppSprocket.Vehicles.Cannons.Editor.CannonEditor), nameof(Il2CppSprocket.Vehicles.Cannons.Editor.CannonEditor.OnGUI))]
static class CannonPanel
{
    static bool logged;
    static void Postfix()
    {
        if (logged) return;
        logged = true;
        Mod.Log.Msg("HELLO_MELON Harmony patch ran (a Cannon panel opened)");
    }
}
