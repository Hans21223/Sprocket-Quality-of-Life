using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Sprocket.ContinuousTracks;
using Sprocket.Engines;
using Sprocket.Vehicles.Powertrains;
using UnityEngine;

namespace SprocketQoL;

/// Test drive recorder (off unless [Diagnostics] Record drives is on): while you drive (a test drive or battle), every physics step of your vehicle's drivetrain goes to
/// a CSV file in BepInEx\SprocketQoL-drives: the engine, each gearbox, the main clutch and split gear, each sprocket and
/// belt, the hull's speed. The Speed & acceleration estimate is checked against these runs (see GearSpeeds). Read straight
/// from the game's own drivetrain state, at the offsets of its data layout (Sprocket 0.2.55.5), so nothing is changed.
public sealed class DriveRecorder : MonoBehaviour
{
    public DriveRecorder(IntPtr pointer) : base(pointer) { }

    static readonly List<TrackBehaviour> tracks = new();
    internal static void Track(TrackBehaviour track) { tracks.RemoveAll(t => t == null || t.Pointer == track.Pointer); tracks.Add(track); }

    VehiclePowertrainBehaviourModule? module;
    Sprocket.Vehicles.VehicleBehaviour? vehicle; // alive while the module may be read
    string vehicleName = "vehicle";
    StreamWriter? file;
    float lookedAt, quietSince;
    static readonly CultureInfo C = CultureInfo.InvariantCulture;

    static float F(IntPtr p, int at) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, at));
    static int I(IntPtr p, int at) => Marshal.ReadInt32(p, at);
    static int B(IntPtr p, int at) => Marshal.ReadByte(p, at);
    static IntPtr Buffer(IntPtr p, int at) => Marshal.ReadIntPtr(p, at); // a NativeArray's data
    static int Length(IntPtr p, int at) => Marshal.ReadInt32(p, at + 8);

    public void FixedUpdate()
    {
        if (Plugin.RecordDrives?.Value != true) { if (file != null) Close(); module = null; vehicle = null; return; }
        try { Step(); }
        catch (Exception ex) { Plugin.ModLog.LogError($"Drive recorder: {ex.Message}"); Close(); }
    }

    void Step()
    {
        // Back in the editor, or the vehicle gone: let go of it at once (its drivetrain's memory may be freed).
        if (DesignEditor.Instance?.IsReady == true || (module != null && vehicle == null)) { module = null; vehicle = null; Close(); return; }
        if (module == null && Time.unscaledTime - lookedAt > 1)
        {
            lookedAt = Time.unscaledTime;
            foreach (var v in FindObjectsOfType<Sprocket.Vehicles.VehicleBehaviour>())
            {
                if (v.ControlType != Sprocket.Vehicles.Control.ControlType.Player || v.modules == null) continue;
                for (int i = 0; i < v.modules.Count && module == null; i++)
                    if (v.modules[i]?.TryCast<VehiclePowertrainBehaviourModule>() is { powertrain: not null } m) { module = m; vehicle = v; vehicleName = v.gameObject.name; }
                if (module != null) break;
            }
        }
        if (module == null) return;
        var powertrain = module.powertrain;
        var body = module.body;
        if (powertrain == null || body == null) { module = null; Close(); return; }
        IntPtr pt = powertrain.Pointer;
        float speed = Vector3.Dot(body.velocity, body.transform.forward);
        float throttleInput = F(pt, 0xB8);
        bool moving = Math.Abs(speed) > 0.05f || Math.Abs(throttleInput) > 0.01f;
        if (!moving)
        {
            if (file != null && Time.time - quietSince > 3) Close();
            return;
        }
        quietSince = Time.time;
        file ??= Open(powertrain, body);
        if (file == null) return;

        var row = new StringBuilder();
        void Add(float v) => row.Append(',').Append(v.ToString("R", C));
        row.Append(Time.fixedTime.ToString("R", C));
        Add(Time.fixedDeltaTime); Add(speed); Add(body.mass); Add(throttleInput); Add(F(pt, 0x18)); Add(F(pt, 0xC8));
        Add(I(pt, 0xAC)); Add(I(pt, 0xC4)); Add(I(pt, 0xC0)); Add(F(pt, 0xA0)); Add(F(pt, 0x90));
        // The engine: speed (rad/s), acceleration, its torque and what the drivetrain takes back, fuel fraction.
        IntPtr engine = powertrain.Engine?.TryCast<EngineBehaviour>()?.Pointer ?? IntPtr.Zero;
        IntPtr es = engine == IntPtr.Zero ? IntPtr.Zero : Buffer(engine, 0x60 + 0x28);
        for (int k = 0; k < 6; k++) Add(es == IntPtr.Zero ? float.NaN : F(es, 4 * k));
        // The layout's main clutch, split gear and chosen gear.
        var l = Layout(powertrain);
        Add(l.State == IntPtr.Zero ? float.NaN : F(l.State, l.Clutch)); Add(l.State == IntPtr.Zero ? float.NaN : F(l.State, l.Slip));
        Add(l.State == IntPtr.Zero || l.Split < 0 ? float.NaN : F(l.State, l.Split)); Add(l.State == IntPtr.Zero ? float.NaN : I(l.State, l.Ideal));
        // Each gearbox: active gear, mode, ratio, engaged fraction, state.
        var boxes = powertrain.Transmissions;
        for (int b = 0; b < 2; b++)
        {
            IntPtr box = boxes != null && b < boxes.Length ? boxes[b]?.TryCast<TransmissionBehaviour>()?.Pointer ?? IntPtr.Zero : IntPtr.Zero;
            IntPtr s = box == IntPtr.Zero ? IntPtr.Zero : Buffer(box, 0x20 + 0x48);
            Add(s == IntPtr.Zero ? float.NaN : B(s, 0x15)); Add(s == IntPtr.Zero ? float.NaN : B(s, 0x14));
            Add(s == IntPtr.Zero ? float.NaN : F(s, 0x10)); Add(s == IntPtr.Zero ? float.NaN : F(s, 8)); Add(s == IntPtr.Zero ? float.NaN : I(s, 0xC));
        }
        // Each track (left first): sprocket speed, belt traction, belt ground speed, tensions, the resistance back to the
        // gearbox; and from the track physics: its load, the hull's speed at it, its rolling resistance.
        for (int side = 0; side < 2; side++)
        {
            IntPtr ps = side == 0 ? l.Left : l.Right, job = l.Outputs == IntPtr.Zero ? IntPtr.Zero : l.Outputs + 0x34 * side;
            if (ps == IntPtr.Zero || job == IntPtr.Zero) { for (int k = 0; k < 10; k++) Add(float.NaN); continue; }
            Add(F(ps, 0x1C)); Add(F(ps, 0xC)); Add(F(ps, 0x3C)); Add(F(ps, 0)); Add(F(ps, 4)); Add(F(ps, 0x38)); Add(F(ps, 0x2C));
            Add(F(job, 0x24)); Add(F(job, 0x20)); Add(F(job, 0x28));
        }
        file.WriteLine(row.ToString());
    }

    /// The layout's drivetrain arrays (twin transmission or clutch-braking): its state, both sides' sprocket jobs (left
    /// first, 0x34 bytes each) and their states, and where the state keeps its main clutch, clutch slip, split gear speed
    /// (none in clutch-braking) and ideal gear.
    static (IntPtr State, IntPtr Outputs, IntPtr Left, IntPtr Right, int Clutch, int Slip, int Split, int Ideal) Layout(PowertrainBehaviour p)
    {
        if (p.TryCast<TwinTransmission>() is { } t) { IntPtr j = t.Pointer + 0xF0; return (Buffer(j, 0x138), Buffer(j, 0x148), Buffer(j, 0x158), Buffer(j, 0x168), 0x14, 0xC, 0, 0x30); }
        if (p.TryCast<ClutchBraking>() is { } c) { IntPtr j = c.Pointer + 0xF8; return (Buffer(j, 0xC0), Buffer(j, 0xD0), Buffer(j, 0xE0), Buffer(j, 0xF0), 8, 0, -1, 0x40); }
        return default;
    }

    StreamWriter? Open(PowertrainBehaviour powertrain, Rigidbody body)
    {
        var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "SprocketQoL-drives");
        Directory.CreateDirectory(dir);
        string name = string.Concat(vehicleName.Replace("(Clone)", "").Trim().Split(Path.GetInvalidFileNameChars()));
        var w = new StreamWriter(Path.Combine(dir, $"{name} {DateTime.Now:yyyyMMdd-HHmmss}.csv"));
        IntPtr pt = powertrain.Pointer;
        string Fs(float v) => v.ToString("R", C);
        w.WriteLine($"# vehicle={name} layout={powertrain.GetIl2CppType().Name} mass={Fs(body.mass)} fixedDeltaTime={Fs(Time.fixedDeltaTime)}");
        var engineB = powertrain.Engine?.TryCast<EngineBehaviour>();
        if (engineB != null)
        {
            IntPtr j = engineB.Pointer + 0x60;
            w.WriteLine($"# engine maxTorque={Fs(F(j, 0))} inertia={Fs(F(j, 4))} health={Fs(F(j, 8))} maxSpeed={Fs(F(j, 0xC))} revLimit={Fs(F(j, 0x14))} friction={Fs(F(j, 0x18))} operatingSpeed={Fs(F(j, 0x1C))} idleSpeed={Fs(F(j, 0x20))}");
            IntPtr table = Buffer(j, 0x38);
            int n = table == IntPtr.Zero ? 0 : Length(j, 0x38);
            // (engine speed in rad/s, torque) points, sampled with a monotone cubic.
            w.WriteLine("# torqueTable=" + string.Join(";", Enumerable.Range(0, Math.Max(0, Math.Min(n, 2048))).Select(k => $"{Fs(F(table, 8 * k))}:{Fs(F(table, 8 * k + 4))}")));
        }
        var boxes = powertrain.Transmissions;
        for (int b = 0; boxes != null && b < boxes.Length; b++)
            if (boxes[b]?.TryCast<TransmissionBehaviour>() is { } box)
            {
                IntPtr j = box.Pointer + 0x20;
                string Gears(int at) => Buffer(j, at) == IntPtr.Zero ? "" : string.Join(";", Enumerable.Range(0, Math.Min(64, Length(j, at))).Select(k => Fs(F(Buffer(j, at), 4 * k))));
                w.WriteLine($"# gearbox{b} drive={Gears(0)} reverse={Gears(0x10)} disengage={Fs(F(j, 0x28))} engage={Fs(F(j, 0x2C))} outputTorqueMultiplier={Fs(F(j, 0x30))} meshType={I(box.Pointer, 0x90)}");
            }
        if (powertrain.TryCast<TwinTransmission>() is { } twin)
        {
            IntPtr j = twin.Pointer + 0xF0;
            w.WriteLine($"# twin clutchStiffness={Fs(F(j, 0))} clutchCapacity={Fs(F(j, 4))} clutchEngageSpeed={Fs(F(j, 0x14))} upshiftEngineSpeed={Fs(F(j, 0x24))} jobMass={Fs(F(j, 0x1C))} gravity={Fs(F(j, 0x18))}");
        }
        if (powertrain.TryCast<ClutchBraking>() is { } cb)
        {
            IntPtr j = cb.Pointer + 0xF8;
            w.WriteLine($"# clutchBraking mainClutchStiffness={Fs(F(j, 0))} mainClutchCapacity={Fs(F(j, 4))} steeringClutchStiffness={Fs(F(j, 8))} steeringClutchCapacity={Fs(F(j, 0xC))} clutchEngageSpeed={Fs(F(j, 0x10C))} upshiftEngineSpeed={Fs(F(j, 0x120))} jobMass={Fs(F(j, 0x118))} gravity={Fs(F(j, 0x114))}");
        }
        var l = Layout(powertrain);
        for (int side = 0; side < 2 && l.Outputs != IntPtr.Zero; side++)
        {
            IntPtr j = l.Outputs + 0x34 * side;
            w.WriteLine($"# sprocket{side} finalDrive={Fs(F(j, 0))} bending={Fs(F(j, 4))} viscous={Fs(F(j, 8))} sprocketInertia={Fs(F(j, 0xC))} maxBrake={Fs(F(j, 0x10))} friction={Fs(F(j, 0x14))} initialTension={Fs(F(j, 0x18))} sprocketRadius={Fs(F(j, 0x1C))} broken={B(j, 0x2C)} side={(sbyte)Marshal.ReadByte(j, 0x2D)} maxTensionOffset={Fs(F(j, 0x30))}");
        }
        foreach (var t in tracks.Where(t => t != null))
        {
            IntPtr info = t.Pointer + 0x1D8;
            w.WriteLine($"# track staticFriction={Fs(F(info, 4))} dynamicFriction={Fs(F(info, 8))} rr={Fs(F(info, 0x2C))};{Fs(F(info, 0x30))};{Fs(F(info, 0x34))};{Fs(F(info, 0x38))};{Fs(F(info, 0x3C))} rrTransfer={Fs(F(info, 0x1C))};{Fs(F(info, 0x20))} beltThickness={Fs(F(info, 0x24))}");
        }
        w.WriteLine("time,dt,speed,mass,throttleInput,throttleTarget,appliedThrottle,action,launchState,shiftState,shiftRpmTarget,inclination," +
                    "engineSpeed,engineAccel,engineAngle,generatedTorque,returnTorque,fuelFraction,mainClutch,clutchSlip,splitSpeed,idealGear," +
                    "g0gear,g0mode,g0ratio,g0engage,g0state,g1gear,g1mode,g1ratio,g1engage,g1state," +
                    "Lsprocket,Ltraction,LbeltGround,Ltight,Lslack,LsmoothedRes,Lbrake,Lnormal,LhullVel,Lrolling," +
                    "Rsprocket,Rtraction,RbeltGround,Rtight,Rslack,RsmoothedRes,Rbrake,Rnormal,RhullVel,Rrolling");
        Plugin.ModLog.LogInfo($"Drive recorder: recording {name} to {dir}");
        return w;
    }

    void Close()
    {
        if (file == null) return;
        file.Dispose();
        file = null;
        Plugin.ModLog.LogInfo("Drive recorder: run saved");
    }
}
