using HarmonyLib;
using MelonLoader;
using Sprocket.Engines;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AmmunitionStorage;
using Sprocket.Vehicles.Engines;
using Sprocket.Vehicles.Tracks;

[assembly: MelonInfo(typeof(SprocketTweaks.Mod), "Sprocket Tweaks", "1.1.0", "Sprocket Mod Kit", null)]
[assembly: MelonGame(null, null)]
[assembly: HarmonyDontPatchAll] // patched one class at a time below, so one failure can't block the rest

namespace SprocketTweaks;

/// Gameplay tweaks as normal Harmony patches (no changes to GameAssembly.dll), each switchable in MelonPreferences:
///   - engine base weight:        the fixed part of every engine's weight (the game's own value is measured, not guessed)
///   - air tyre weight factor:    air tyre wheels weigh this fraction of a solid wheel
///   - empty ammo racks are safe: a rack with no shells left doesn't react to damage, so it can't cook off
/// Weights are adjusted where every part reports them (VehicleComponent.SetMass); small helper functions like
/// EngineRules.CalculateMass get merged into their callers in the game build, so hooking those alone does nothing.
/// Patches target the game's Sprocket.* classes (not MLLoader's Il2CppSprocket.* copies), so they combine with BepInEx mods.
public sealed class Mod : MelonMod
{
    internal static MelonLogger.Instance Log = null!;
    internal static MelonPreferences_Entry<bool> EngineOn = null!, TyresOn = null!, RacksOn = null!;
    internal static MelonPreferences_Entry<float> EngineBaseKg = null!, TyreFactor = null!;

    public override void OnInitializeMelon()
    {
        Log = LoggerInstance;
        var settings = MelonPreferences.CreateCategory("SprocketTweaks", "Sprocket Tweaks");
        EngineOn = settings.CreateEntry("EngineBaseWeightEnabled", true, "Change engine base weight", null, false, false, null);
        EngineBaseKg = settings.CreateEntry("EngineBaseWeightKg", 30f, "Engine base weight (kg)", "Replaces the fixed part of every engine's weight.", false, false, null);
        TyresOn = settings.CreateEntry("AirTyreWeightEnabled", true, "Change air tyre weight", null, false, false, null);
        TyreFactor = settings.CreateEntry("AirTyreWeightFactor", 0.05f, "Air tyre weight factor", "1 = same as a solid wheel.", false, false, null);
        RacksOn = settings.CreateEntry("EmptyRacksAreSafe", true, "Empty ammo racks can't cook off", null, false, false, null);
        foreach (var patch in new[] { typeof(PartWeights), typeof(WheelBuild), typeof(EmptyRacks) })
        {
            try { HarmonyInstance.CreateClassProcessor(patch).Patch(); }
            catch (Exception ex) { Log.Error($"TWEAKS {patch.Name} could not attach: {ex.Message}"); }
        }
        Log.Msg($"TWEAKS 1.1 loaded: engine base {(EngineOn.Value ? EngineBaseKg.Value + " kg" : "off")}, " +
                $"air tyres {(TyresOn.Value ? "x" + TyreFactor.Value : "off")}, empty racks safe {RacksOn.Value}");
    }
}

/// Every vehicle part reports its weight through SetMass while it builds: adjust engines and air tyre wheels there.
[HarmonyPatch(typeof(VehicleComponent), nameof(VehicleComponent.SetMass))]
static class PartWeights
{
    static readonly HashSet<string> probed = new();
    static float? engineBase;   // the game's weight for a zero-size engine = its fixed base
    static bool engineReported, tyreReported;

    static void Prefix(VehicleComponent __instance, ref float value, MassType massType)
    {
        // Probe: the first report from each kind of part, to map the game's weight path in the log.
        string kind = __instance.GetIl2CppType().Name;
        if (probed.Count < 30 && probed.Add(kind + massType)) Mod.Log.Msg($"TWEAKS probe: {kind} reports {value:0.#} kg ({massType})");

        if (Mod.EngineOn.Value && massType == MassType.Powertrain && __instance.TryCast<CombustionEngine>() is { } engine)
            AdjustEngine(engine, ref value);
        else if (WheelBuild.AirTyre && massType == MassType.RunningGear)
        {
            float before = value;
            value *= Mod.TyreFactor.Value;
            if (!tyreReported) { tyreReported = true; Mod.Log.Msg($"TWEAKS air tyre: {kind} {before:0.#} kg -> {value:0.#} kg"); }
        }
    }

    static void AdjustEngine(CombustionEngine engine, ref float value)
    {
        engineBase ??= EngineRules.CalculateMass(0f);
        float formula = EngineRules.CalculateMass(engine.Blueprint.Volume);
        // Only the engine's own body weight (it equals the game's formula); other powertrain weights pass through.
        if (engineBase < 1f || Math.Abs(value - formula) > Math.Max(0.5f, formula * 0.01f))
        {
            if (!engineReported) { engineReported = true; Mod.Log.Msg($"TWEAKS engine: reported {value:0.#} kg, formula {formula:0.#} kg, base {engineBase:0.#} kg -> left unchanged"); }
            return;
        }
        float before = value;
        value = Math.Max(1f, value - engineBase.Value + Mod.EngineBaseKg.Value);
        if (!engineReported) { engineReported = true; Mod.Log.Msg($"TWEAKS engine: base {engineBase:0.#} kg -> {Mod.EngineBaseKg.Value:0.#} kg ({before:0} kg engine now {value:0} kg)"); }
    }
}

/// While a wheel set builds, remember whether its wheels are the air tyre model (Parts\airTyreWheel.json's mesh).
[HarmonyPatch(typeof(WheelArray), nameof(WheelArray.Build))]
static class WheelBuild
{
    const string AirTyreMesh = "cfdda3c1c8490524f89f517142fa8e70";
    [ThreadStatic] internal static bool AirTyre;
    static void Prefix(ref WheelArrayBlueprint blueprint) => AirTyre = Mod.TyresOn.Value && blueprint?.MeshGuid == AirTyreMesh;
    static void Postfix() => AirTyre = false;
}

[HarmonyPatch(typeof(AmmoRack), nameof(AmmoRack.OnHealthChanged))]
static class EmptyRacks
{
    static bool reported;

    // Returning false skips the game's damage reaction for this rack only: with no shells there's nothing to ignite.
    static bool Prefix(AmmoRack __instance)
    {
        if (!Mod.RacksOn.Value) return true;
        var rack = __instance.behaviour;
        if (rack == null || rack.AmountStored > 0) return true;
        if (!reported) { reported = true; Mod.Log.Msg("TWEAKS empty rack hit: no cook-off"); }
        return false;
    }
}
