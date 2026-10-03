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

using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// Powertrain: a "Speed & acceleration" section in the Transmission and Engine panels with every gear's top speed and
/// the acceleration from a standstill, worked out with the game's own drivetrain maths (DriveSim) on this design's parts.
[HarmonyPatch]
public static class GearSpeeds
{
    const string Title = "Speed & acceleration";
    static string? shown, logged; // last state / inputs written to the log, so each is logged once per change
    static CombustionEngineComponentEditor? pendingEngineRedraw;
    static TransmissionEditor? pendingGearsRedraw;
    static string? predictionKey, predictionText;

    // After a change the panel isn't redrawn by itself: ask, so the numbers follow.
    // If the user is dragging a slider, defer redraw until the mouse button is released,
    // otherwise destroying the slider mid-drag aborts the drag and locks the slider.
    [HarmonyPostfix, HarmonyPatch(typeof(TransmissionEditor), nameof(TransmissionEditor.OnComponentRebuilt))]
    static void GearsChanged(TransmissionEditor __instance) => Ui.Guard(Title, () =>
    {
        if (IsDragging()) pendingGearsRedraw = __instance;
        else __instance.RequestRedraw();
    });

    [HarmonyPostfix, HarmonyPatch(typeof(CombustionEngineComponentEditor), nameof(CombustionEngineComponentEditor.OnComponentRebuilt))]
    static void EngineChanged(CombustionEngineComponentEditor __instance) => Ui.Guard(Title, () =>
    {
        if (IsDragging()) pendingEngineRedraw = __instance;
        else __instance.RequestRedraw();
    });

    internal static void Update()
    {
        if (accelTask is { IsCompleted: true } done && !IsDragging())
        {
            accelTask = null;
            if (done.IsFaulted) Plugin.ModLog.LogWarning($"{Title}: acceleration run failed: {done.Exception?.GetBaseException()}");
            accelText = done.IsFaulted ? "Acceleration: couldn't work it out (see the log)." : done.Result;
            try { redrawPanel?.Invoke(); } catch { }
        }
        if (pendingEngineRedraw != null && !IsDragging())
        {
            var e = pendingEngineRedraw;
            pendingEngineRedraw = null;
            try { e.RequestRedraw(); } catch { }
        }
        if (pendingGearsRedraw != null && !IsDragging())
        {
            var g = pendingGearsRedraw;
            pendingGearsRedraw = null;
            try { g.RequestRedraw(); } catch { }
        }
    }

    internal static void LeftEditor()
    {
        pendingEngineRedraw = null;
        pendingGearsRedraw = null;
        predictionKey = null;
        predictionText = null;
        accelKey = accelText = null;
        accelTask = null;
        redrawPanel = null;
    }

    static bool IsDragging()
    {
        try
        {
            if (Mouse.current is { } m && m.leftButton.isPressed) return true;
        }
        catch { }
        try
        {
            if (Input.GetMouseButton(0)) return true;
        }
        catch { }
        return false;
    }

    internal static void InTransmission(TransmissionEditor __instance, Panel layout) =>
        Ui.Inspector(Title, layout, () =>
        {
            redrawPanel = () => __instance.RequestRedraw();
            Draw(layout, __instance.Component, null, __instance.Component.Vehicle?.Mass ?? 0);
        });

    internal static void InEngine(CombustionEngineComponentEditor __instance, Panel layout) =>
        Ui.Inspector(Title, layout, () =>
        {
            redrawPanel = () => __instance.RequestRedraw();
            Draw(layout, null, __instance.blueprint, __instance.Component.Vehicle?.Mass ?? 0);
        });

    static void Draw(Panel layout, TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var ui = Ui.Drawer(layout);
        if (ui == null) return;
        var text = Describe(gearbox, engine, mass);
        var summary = string.Join(" | ", text.Split('\n').Where(l => !l.StartsWith("Gear")));
        if (summary != shown) { shown = summary; Plugin.ModLog.LogInfo($"{Title}: {shown}"); }
        Ui.Section(layout, Title);
        // The shared drawer accounts for the current inspector width.
        ui.InfoField(text, text.Split('\n').Length);
    }

    // Each track's behaviour as a drive starts, for the drive recorder.
    [HarmonyPostfix, HarmonyPatch(typeof(TrackAssembly), nameof(TrackAssembly.EnableBehaviour))]
    static void TrackStarted(TrackAssembly __instance) => Ui.Guard(Title, () =>
    {
        if (__instance.Controller?.TryCast<TrackBehaviour>() is { } track) DriveRecorder.Track(track);
    });

    static float Try(Func<float> read, float fallback = 0) { try { return read(); } catch { return fallback; } }

    /// The engine never revs past its rev limit: its own setting, or (by default) the upshift rpm + 50.
    internal static float RevLimit(EngineBlueprint engine)
    {
        float limit = Try(() => engine.RevLimit);
        return limit <= 0 || limit > engine.MaxRPM ? engine.MaxRPM : limit;
    }

    /// Disengaging and engaging time of the gearbox's type (synchromesh, constant or sliding mesh).
    static (float Disengage, float Engage) ShiftTimes(TransmissionBlock gearbox)
    {
        try
        {
            return TransmissionMeshTypes.ParseMeshType(gearbox.Blueprint.meshType) switch
            {
                TransmissionMeshType.Synchromesh => (TransmissionBehaviour.SynchromeshDisengageTime, TransmissionBehaviour.SynchromeshEngageTime),
                TransmissionMeshType.ConstantMesh => (TransmissionBehaviour.ConstantMeshDisengageTime, TransmissionBehaviour.ConstantMeshEngageTime),
                _ => (TransmissionBehaviour.SlidingMeshDisengageTime, TransmissionBehaviour.SlidingMeshEngageTime),
            };
        }
        catch { return (0, 0); }
    }

    const float Rads = MathF.PI / 30; // rpm -> rad/s

    /// The vehicle as the game sets it up for a drive: the engine job (EngineBehaviour.SetBlueprint: the torque template
    /// scaled to max rpm and torque, the downshift rpm as its operating speed), the twin transmission's clutch (5x and 1.5x
    /// the max torque), and each track as TrackAssembly.CreateTrackController makes it: the belt wraps the sprocket at its
    /// belt wrap radius, bending resistance is the segment mass², sprocket drag 0.25 x segment mass + 3 (each times the
    /// track technology's coefficient), rolling resistance 0.03 (1 + 0.001 v²), dynamic friction 0.8. Values per side,
    /// however many tracks; the mass is the one the vehicle's rigidbody gets.
    static DriveSim.Vehicle Build(EngineBlueprint engine, float[] ratios, TransmissionBlock gearbox, List<TrackAssembly> tracks, float finalDrive, float radius, float mass)
    {
        float revLimit = RevLimit(engine);
        var e = DriveSim.Engine.Of(engine.MaxTorque, engine.MaxRPM * Rads, Try(() => engine.Inertia), revLimit * Rads,
            Try(() => engine.FrictionCoefficient), Try(() => engine.Downshift) * Rads, engine.IdleRPM * Rads);
        var (disengage, engage) = ShiftTimes(gearbox);
        float Tech(TrackAssembly t, string key) => Try(() => t.TrackTech?.GetFloat(key, 1) ?? 1, 1);
        float Segment(TrackAssembly t) => Try(() => t.Belt.SegmentMass);
        float PerSide(Func<TrackAssembly, float> f) => tracks.Sum(f) / 2;
        var sprocket = new DriveSim.Sprocket(finalDrive,
            PerSide(t => Tech(t, "bendingResistanceCoefficient") * Segment(t) * Segment(t)),
            PerSide(t => Tech(t, "viscousDragCoefficient") * (0.25f * Segment(t) + 3)),
            PerSide(t => Try(() => t.ComputeSprocketInertia())), 0.8f, radius);
        float rolling = Tech(tracks[0], "rollingResistanceCoefficient");
        // An upshift at or past the rev limit is taken just under it (the engine never gets there).
        float upshift = Math.Min(engine.Upshift, revLimit - 25) * Rads;
        float torque = engine.MaxTorque, step = Try(() => Time.fixedDeltaTime, 0.01f);
        return new DriveSim.Vehicle(e, ratios, disengage, engage, 5 * torque, 1.5f * torque, upshift, sprocket, mass, 0.03f * rolling, 0.001f * rolling,
            FixedDeltaTime: step > 0 ? step : 0.01f);
    }

    static string Inputs(DriveSim.Vehicle v, float[] reverse)
    {
        var e = v.Engine; var p = v.Sprocket;
        static string F(float x) => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return $"engine {F(e.MaxTorque)} N·m to {F(e.CurveSpeed[^2] / Rads)} rpm, rev limit {F(e.RevLimit / Rads)}, upshift {F(v.UpshiftSpeed / Rads)}, " +
               $"operating {F(e.OperatingSpeed / Rads)}, idle {F(e.IdleSpeed / Rads)}, inertia {F(e.Inertia)}, friction {F(e.Friction)}; " +
               $"gears {string.Join("/", v.Gears.Select(F))} (reverse {string.Join("/", reverse.Select(F))}), shift {F(v.Disengage)} + {F(v.Engage)} s; " +
               $"final drive {F(p.FinalDrive)}, belt radius {F(p.Radius)}, per side bending {F(p.Bending)}, drag {F(p.Viscous)}, sprocket inertia {F(p.Inertia)}; " +
               $"rolling {F(v.RollingResistance)} (1 + {F(v.RollingPerSpeed2)} v²); mass {F(v.Mass)} kg; step {F(v.FixedDeltaTime)} s";
    }

    // The acceleration run takes a moment, so it runs in the background; the panel shows it once it's done.
    static string? accelKey, accelText;
    static Task<string>? accelTask;
    static Action? redrawPanel;

    static string Acceleration(DriveSim.Vehicle vehicle, string key, float top)
    {
        if (accelKey == key && accelText != null) return accelText;
        if (accelKey != key || accelTask == null)
        {
            accelKey = key; accelText = null;
            accelTask = Task.Run(() => Milestones(vehicle, top));
        }
        return "Working out the acceleration...";
    }

    /// Seconds to each round speed on the way to top speed (0-20, 0-40... or 0-10, 0-20... for slow vehicles).
    static string Milestones(DriveSim.Vehicle vehicle, float top)
    {
        var run = DriveSim.Accelerate(vehicle);
        float topKmh = top * 3.6f, step = topKmh > 45 ? 20 : 10;
        var parts = new List<string>();
        for (float kmh = step; kmh < topKmh * 0.97f; kmh += step)
        {
            int i = run.FindIndex(r => r.Speed * 3.6f >= kmh);
            if (i < 0) break;
            float t = run[i].Time;
            parts.Add($"0-{kmh:0} km/h: {(t < 10 ? t.ToString("0.0") : t.ToString("0"))} s");
        }
        return parts.Count == 0 ? "Acceleration: too slow to time." : string.Join("  ·  ", parts);
    }

    /// Each gear's top speed, solved where the drive settles (DriveSim.TopSpeed), the fastest of them, peak power under
    /// the rev limit, and the acceleration from a standstill (DriveSim.Accelerate, in the background).
    static string Describe(TransmissionBlock? gearbox, EngineBlueprint? engine, float mass)
    {
        var parts = DesignEditor.Instance?.AllComponents().ToList() ?? new();
        var engines = parts.Select(c => c.TryCast<CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
        engine ??= (engines.FirstOrDefault(e => e!.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;
        var gearboxes = parts.Select(c => c.TryCast<TransmissionBlock>()).Where(t => t != null).ToList();
        gearbox ??= gearboxes.FirstOrDefault(t => t!.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();
        var tracks = parts.Select(c => c.TryCast<TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).Select(t => t!).ToList();
        if (VehicleOf(engine, gearbox, tracks, mass, out var reverse, out var problem) is not { } vehicle) return problem;
        string key = Inputs(vehicle, reverse);
        if (key != predictionKey || predictionText == null)
        {
            if (key != logged) { logged = key; Plugin.ModLog.LogInfo($"{Title} inputs: {key}"); }
            predictionKey = key;
            predictionText = Speeds(vehicle, reverse, out topSpeed);
        }
        return predictionText + "\n" + (topSpeed > 0 ? Acceleration(vehicle, key, topSpeed) : "Can't get moving in any gear.") +
               "\nLevel ground, full throttle, automatic gears: the game's own drivetrain maths for this design, step by step.";
    }

    static float topSpeed;

    /// The vehicle as the game sets it up for a drive (Build), from its engine, gearbox, tracks and mass; null, with
    /// what's missing, if it can't be. `reverse`: the reverse gears' ratios.
    static DriveSim.Vehicle? VehicleOf(EngineBlueprint? engine, TransmissionBlock? gearbox, List<TrackAssembly> tracks, float mass, out float[] reverse, out string problem)
    {
        reverse = Array.Empty<float>();
        problem = engine == null ? "Add an engine to see speeds." : gearbox == null ? "Add a transmission to see speeds." : tracks.Count == 0 ? "Add tracks to see speeds." : "";
        if (engine == null || gearbox == null || tracks.Count == 0) return null;
        var ratios = (gearbox.resultingDriveGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        reverse = (gearbox.resultingReverseGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
        if (ratios.Length == 0) { problem = "No drive gears yet."; return null; }
        var track = tracks[0];
        float finalDrive = track.BlueprintSlot.Blueprint.FinalDriveRatio;
        float radius = Try(() => track.SprocketAssembly.BeltWrapRadius);
        if (radius <= 0 || finalDrive <= 0 || engine.MaxRPM <= 0 || engine.MaxTorque <= 0) { problem = "Can't read the tracks' drive sprocket yet."; return null; }
        if (mass <= 0) { problem = "Can't read the vehicle's mass yet."; return null; }
        return Build(engine, ratios, gearbox, tracks, finalDrive, radius, mass);
    }

    /// Top speed forward and in reverse (km/h) on level ground, as the Speed & acceleration panel works it out (the
    /// fastest gear where the drive settles; 0 for none); null if the drivetrain can't be read.
    internal static (float Forward, float Reverse)? TopSpeeds(EngineBlueprint? engine, TransmissionBlock? gearbox, List<TrackAssembly> tracks, float mass)
    {
        if (VehicleOf(engine, gearbox, tracks, mass, out var reverse, out _) is not { } v) return null;
        float forward = v.Gears.Max(r => DriveSim.TopSpeed(v, r).Speed) * 3.6f;
        float back = reverse.Length > 0 ? DriveSim.TopSpeed(v, reverse.Min()).Speed * 3.6f : 0;
        return (forward, back);
    }

    static string Speeds(DriveSim.Vehicle vehicle, float[] reverse, out float top)
    {
        var text = new StringBuilder();
        var gears = vehicle.Gears.Select(r => DriveSim.TopSpeed(vehicle, r)).ToArray();
        float limited = vehicle.Engine.RevLimit - 2 * 5.235988f; // into the rev limiter's band: out of revs, not power
        for (int i = 0; i < gears.Length; i++)
            text.Append(gears[i].Speed <= 0 ? $"Gear {i + 1}: can't move the vehicle\n"
                : $"Gear {i + 1}: {gears[i].Speed * 3.6f:0.0} km/h{(gears[i].EngineSpeed < limited ? " (short of the rev limit)" : "")}\n");
        if (reverse.Length > 0)
        {
            var back = DriveSim.TopSpeed(vehicle, reverse.Min());
            text.Append(back.Speed > 0 ? $"Reverse: {back.Speed * 3.6f:0.0} km/h\n" : "Reverse: can't move the vehicle\n");
        }
        int best = 0;
        for (int i = 1; i < gears.Length; i++) if (gears[i].Speed > gears[best].Speed) best = i;
        top = gears[best].Speed;
        if (top > 0)
            text.Append($"Top speed: {top * 3.6f:0.0} km/h in gear {best + 1} ({gears[best].EngineSpeed / Rads:0} rpm, track slip {gears[best].Slip / top * 100:0.0}%)\n");
        // The most power the engine gives below its rev limit (not the engine's rated figure at max revs).
        var e = vehicle.Engine;
        var (power, at) = Enumerable.Range(0, 201).Select(k => e.IdleSpeed + (e.RevLimit - e.IdleSpeed) * k / 200f)
            .Select(w => (Kw: e.FullThrottle(w) * w / 1000, Rpm: w / Rads)).MaxBy(x => x.Kw);
        text.Append($"Usable peak power: {power:0} kW / {power * 1.341f:0} hp at {at:0} rpm");
        return text.ToString();
    }
}
