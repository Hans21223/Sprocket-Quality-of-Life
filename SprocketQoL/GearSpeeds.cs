using System.Text;
using HarmonyLib;
using Sprocket.ContinuousTracks;
using Sprocket.Engines;
using Sprocket.Powertrains.Transmissions;
using Sprocket.UI;
using Sprocket.VehicleDesigner.Powertrains;
using Sprocket.Vehicles.Engines;
using Sprocket.Vehicles.Engines.Editor;
using Sprocket.Vehicles.PhysicsSystems;
using Sprocket.Vehicles.Powertrains;
using Sprocket.Vehicles.Tracks;
using Sprocket.Vehicles.Transmissions;
using Sprocket.Vehicles.Transmissions.Editor;

namespace SprocketQoL;

/// Powertrain: a "Speed & acceleration" section in the Transmission and Engine panels with every gear's top speed
/// (the game's own formula, capped by the tracks' speed limit) and how long the vehicle takes to reach top speed.
[HarmonyPatch]
public static class GearSpeeds
{
    const string Title = "Speed & acceleration";
    static string? shown, logged; // last state / inputs written to the log, so each is logged once per change

    // After a change the panel isn't redrawn by itself: ask, so the numbers follow.
    [HarmonyPostfix, HarmonyPatch(typeof(TransmissionEditor), nameof(TransmissionEditor.OnComponentRebuilt))]
    static void GearsChanged(TransmissionEditor __instance) => Ui.Guard(Title, __instance.RequestRedraw);

    [HarmonyPostfix, HarmonyPatch(typeof(CombustionEngineComponentEditor), nameof(CombustionEngineComponentEditor.OnComponentRebuilt))]
    static void EngineChanged(CombustionEngineComponentEditor __instance) => Ui.Guard(Title, __instance.RequestRedraw);

    [HarmonyPostfix, HarmonyPatch(typeof(TransmissionEditor), nameof(TransmissionEditor.OnGUI))]
    static void InTransmission(TransmissionEditor __instance, IGUILayout layout) =>
        Ui.Guard(Title, () => Draw(layout, __instance.Component, null, __instance.Component.Vehicle?.Mass ?? 0));

    [HarmonyPostfix, HarmonyPatch(typeof(CombustionEngineComponentEditor), nameof(CombustionEngineComponentEditor.OnGUI))]
    static void InEngine(CombustionEngineComponentEditor __instance, IGUILayout layout) =>
        Ui.Guard(Title, () => Draw(layout, null, __instance.blueprint, __instance.Component.Vehicle?.Mass ?? 0));

    static void Draw(IGUILayout layout, TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null) return;
        var text = Describe(gearbox, engine, mass);
        var summary = string.Join(" | ", text.Split('\n').Where(l => !l.StartsWith("Gear")));
        if (summary != shown) { shown = summary; Plugin.ModLog.LogInfo($"{Title}: {shown}"); }
        Ui.Section(layout, Title);
        ui.InfoField(text, text.Split('\n').Length); // every line is short, so none wraps past the space given
    }

    /// Everything the drive needs, read straight off the parts.
    sealed record Drive(EngineBlueprint Engine, float[] Ratios, float FinalDrive, float Radius, float Mass, float Limit,
                        float ShiftTime, float EngineInertia, float SprocketInertia, float Drag, RollingResistanceParameters? Rolling, int Tracks);

    /// Gear ratios (this transmission, else the powertrain's), the engine (this one, else the powertrain's), the
    /// tracks' final drive, drive sprocket and speed limit.
    static string Describe(TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var parts = DesignEditor.Instance?.AllComponents().ToList() ?? new();
        var engines = parts.Select(c => c.TryCast<CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
        engine ??= (engines.FirstOrDefault(e => e!.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;
        var gearboxes = parts.Select(c => c.TryCast<TransmissionBlock>()).Where(t => t != null).ToList();
        gearbox ??= gearboxes.FirstOrDefault(t => t!.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();
        var tracks = parts.Select(c => c.TryCast<TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).ToList();
        var track = tracks.FirstOrDefault();
        if (engine == null) return "Add an engine to see speeds.";
        if (gearbox == null) return "Add a transmission to see speeds.";
        if (track == null) return "Add tracks to see speeds.";
        var ratios = (gearbox.resultingDriveGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        var reverse = (gearbox.resultingReverseGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        if (ratios.Length == 0) return "No drive gears yet.";
        float finalDrive = track.BlueprintSlot.Blueprint.FinalDriveRatio;
        var sprocket = track.SprocketAssembly?.WheelBlueprint;
        float radius = sprocket?.HasBlueprint == true ? sprocket.Blueprint.Radius : 0;
        if (radius <= 0 || finalDrive <= 0 || engine.MaxRPM <= 0) return "Can't read the tracks' drive sprocket yet.";

        var d = new Drive(engine, ratios, finalDrive, radius, mass, Try(() => track.TopSpeed), ShiftTime(gearbox), Try(() => engine.Inertia),
            tracks.Sum(t => Try(() => t!.ComputeSprocketInertia())), Try(() => VehiclePhysics.DefaultLinearDrag), Rolling(track), Math.Max(1, tracks.Count));
        string inputs = $"{engine.MaxRPM} rpm (idle {engine.IdleRPM}), {engine.MaxTorque:0} torque, gears {string.Join("/", ratios.Select(r => r.ToString("0.##")))}, " +
                        $"final drive {finalDrive:0.##}, sprocket radius {radius:0.###}, mass {mass:0} kg, track limit {d.Limit * 3.6f:0.#} km/h, shift {d.ShiftTime:0.##} s, " +
                        $"engine inertia {d.EngineInertia:0.###}, sprocket inertia {d.SprocketInertia:0.###}, drag {d.Drag:0.####}, " +
                        $"rolling {(d.Rolling == null ? "not available" : $"{d.Rolling.Value.ComputeLongitudinalRollingResistance(mass * 9.81f, 10):0} N at 10 m/s")}, {tracks.Count} tracks";
        if (inputs != logged) { logged = inputs; Plugin.ModLog.LogInfo($"{Title} inputs: {inputs}"); }

        float limit = d.Limit > 0 ? d.Limit * 3.6f : float.MaxValue;
        float Speed(float ratio) => PowertrainInfo.CalculateSpeed(engine.MaxRPM, ratio * finalDrive, radius) * 3.6f; // m/s -> km/h
        var text = new StringBuilder();
        for (int i = 0; i < ratios.Length; i++)
            text.Append(Speed(ratios[i]) > limit ? $"Gear {i + 1}:  {limit:0} km/h (track limit; gearing {Speed(ratios[i]):0})\n" : $"Gear {i + 1}:  {Speed(ratios[i]):0} km/h\n");
        if (reverse.Length > 0) text.Append($"Reverse:  {Math.Min(Speed(reverse.Min()), limit):0} km/h\n");
        if (mass <= 0) return text.ToString().TrimEnd('\n');
        var (seconds, reached, shifts) = Accelerate(d);
        text.Append($"0 to {reached * 3.6f:0} km/h in about {seconds:0} s ({shifts} shifts)\n");
        text.Append(d.Rolling == null ? "Flat ground, full power; no track rolling drag." : "Flat ground, full power, best gear.");
        return text.ToString();
    }

    static float Try(Func<float> read) { try { return read(); } catch { return 0; } }

    /// Engaging plus disengaging time of the gearbox's type (synchromesh, constant or sliding mesh).
    static float ShiftTime(TransmissionBlock gearbox) => Try(() => TransmissionMeshTypes.ParseMeshType(gearbox.Blueprint.meshType) switch
    {
        TransmissionMeshType.Synchromesh => TransmissionBehaviour.SynchromeshDisengageTime + TransmissionBehaviour.SynchromeshEngageTime,
        TransmissionMeshType.ConstantMesh => TransmissionBehaviour.ConstantMeshDisengageTime + TransmissionBehaviour.ConstantMeshEngageTime,
        _ => TransmissionBehaviour.SlidingMeshDisengageTime + TransmissionBehaviour.SlidingMeshEngageTime,
    });

    /// The tracks' own rolling resistance numbers, when the game has set them up (a driving vehicle).
    static RollingResistanceParameters? Rolling(TrackAssembly track)
    {
        try { return track.Controller?.TryCast<TrackBehaviour>()?.UpdateInfo.rrParameters; }
        catch { return null; }
    }

    /// Seconds from standing to top speed on flat ground at full throttle: the engine's torque at its current revs (the
    /// game's power curve) through whichever gear pushes hardest, less rolling resistance and drag, on the vehicle's
    /// mass plus the engine and sprockets spinning up; no drive while changing gear. Stops at the tracks' speed limit,
    /// the top gear's max revs, or where drag and rolling resistance use up all the push.
    static (float Seconds, float Reached, int Shifts) Accelerate(Drive d)
    {
        var e = d.Engine;
        float idle = Math.Clamp(e.IdleRPM, 1, e.MaxRPM - 1), max = e.MaxRPM;
        static float Omega(float rpm) => rpm * MathF.PI / 30; // rad/s
        // Torque across the rev range from the game's power figures, scaled so its peak is the engine's max torque
        // (works whatever unit the power comes back in).
        var torque = Enumerable.Range(0, 101).Select(k => idle + (max - idle) * k / 100f)
            .Select(rpm => EngineRules.CalculatePowerAtRPM(e.MaxTorque, rpm, max) / Omega(rpm)).ToArray();
        float peak = torque.Max();
        if (peak <= 0) return (float.NaN, 0, 0);
        float TorqueAt(float rpm)
        {
            float x = Math.Clamp((rpm - idle) / (max - idle), 0, 1) * 100;
            int k = Math.Min((int)x, 99);
            return e.MaxTorque * (torque[k] + (torque[k + 1] - torque[k]) * (x - k)) / peak;
        }
        float Resist(float v) => (d.Rolling?.ComputeLongitudinalRollingResistance(d.Mass * 9.81f / d.Tracks, v) ?? 0) * d.Tracks + d.Drag * d.Mass * v;

        float top = Omega(max) / (d.Ratios.Min() * d.FinalDrive) * d.Radius;
        if (d.Limit > 0) top = Math.Min(top, d.Limit);
        float v = 0, t = 0;
        int gear = -1, shifts = 0;
        const float dt = 0.02f;
        while (v < top * 0.995f && t < 600)
        {
            // The gear that pushes hardest right now, counting the engine it has to spin up.
            float best = 0;
            int pick = -1;
            for (int i = 0; i < d.Ratios.Length; i++)
            {
                float g = d.Ratios[i] * d.FinalDrive / d.Radius;          // sprocket-to-ground and gearing, per metre
                float rpm = v * g * 30 / MathF.PI;
                if (rpm > max) continue;
                float a = TorqueAt(rpm) * g / (d.Mass + d.EngineInertia * g * g + d.SprocketInertia / (d.Radius * d.Radius));
                if (a > best) { best = a; pick = i; }
            }
            if (pick < 0) break;
            if (gear >= 0 && pick != gear)
            {
                // Changing gear: nothing drives, drag and rolling resistance still slow the vehicle.
                shifts++;
                for (float s = 0; s < d.ShiftTime; s += dt) { v = Math.Max(0, v - Resist(v) / d.Mass * dt); t += dt; }
            }
            gear = pick;
            float net = best - Resist(v) / d.Mass;
            if (net <= 1e-3f) break;                                           // it can't go any faster
            v += net * dt;
            t += dt;
        }
        return (t, v, shifts);
    }
}
