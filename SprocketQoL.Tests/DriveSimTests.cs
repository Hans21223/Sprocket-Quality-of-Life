using SprocketQoL;

/// The drivetrain replica against a recorded test drive (RGM-167F, 61.2 t, twin transmission, Sprocket 0.2.55.5): its
/// settings as the game's jobs held them, and what the game did with them on level ground at full throttle.
static class DriveSimTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception("drive: " + message); }

    static DriveSim.Vehicle Recorded(float mass = 61186.195f) => new(
        DriveSim.Engine.Of(3114.3398f, 339.92035f, 19.130508f, 298.45132f, 1.0791776f, 209.43951f, 67.96312f),
        new[] { 5, 2.5f, 1.6666666f, 1.25f, 1, 0.8333333f, 0.71428573f, 0.625f }, 0.1f, 0.3f, 15571.699f, 4671.51f, 293.21533f,
        new DriveSim.Sprocket(10, 2538.1084f, 15.594911f, 913.06604f, 0.8f, 0.37624997f), mass, 0.03f, 0.001f);

    public static void Run()
    {
        var v = Recorded();
        // The engine's torque table: the game's template scaled by max speed and torque (as the engine job held it).
        Check(MathF.Abs(v.Engine.CurveSpeed[4] - 135.96814f) < 0.01f && MathF.Abs(v.Engine.CurveTorque[4] - 2647.189f) < 0.1f
              && MathF.Abs(DriveSim.Sample(v.Engine.CurveSpeed, v.Engine.CurveTorque, 271.93628f) - 3114.3398f) < 0.1f, "torque table");

        // Top speed: the game cruised at 62.54 km/h, 2827 rpm, 2.6% track slip.
        var top = DriveSim.TopSpeed(v, v.Gears[^1]);
        Check(MathF.Abs(top.Speed * 3.6f / 62.54f - 1) < 0.005f, $"top speed {top.Speed * 3.6f:0.00} km/h (game 62.54)");
        Check(MathF.Abs(top.EngineSpeed * 30 / MathF.PI - 2827) < 15, $"engine at top speed {top.EngineSpeed * 30 / MathF.PI:0} rpm (game 2827)");

        // Full throttle from a standstill: every gear in turn, settling where the solver says, the game's times.
        var run = DriveSim.Accelerate(v);
        int shifts = 0;
        for (int i = 1; i < run.Count; i++) if (run[i].Gear != run[i - 1].Gear) shifts++;
        Check(shifts == v.Gears.Length - 1, $"one shift into each higher gear ({shifts})");
        Check(MathF.Abs(run[^1].Speed / top.Speed - 1) < 0.003f, $"settles at the solved top speed ({run[^1].Speed * 3.6f:0.00} vs {top.Speed * 3.6f:0.00} km/h)");
        float At(float kmh) => run.First(r => r.Speed * 3.6f >= kmh).Time;
        Check(MathF.Abs(At(40) / 24.60f - 1) < 0.05f && MathF.Abs(At(60) / 69.66f - 1) < 0.05f,
              $"0-40 in {At(40):0.0} s (game 24.6), 0-60 in {At(60):0.0} s (game 69.7)");

        // Four times as heavy: the top gears can't pull, so it stays in the best gear it can use.
        var heavy = Recorded(4 * 61186.195f);
        var gears = heavy.Gears.Select(r => DriveSim.TopSpeed(heavy, r).Speed).ToArray();
        int best = Array.IndexOf(gears, gears.Max());
        Check(best < heavy.Gears.Length - 1 && gears[^1] < gears[best], $"too heavy for top gear (best gear {best + 1})");
        var slow = DriveSim.Accelerate(heavy);
        Check(slow[^1].Gear == best && MathF.Abs(slow[^1].Speed / gears[best] - 1) < 0.01f,
              $"heavy: settles in gear {slow[^1].Gear + 1} at {slow[^1].Speed * 3.6f:0.0} km/h (solved: gear {best + 1}, {gears[best] * 3.6f:0.0})");

        // Far too heavy to move: no gear can, and the drive gives up rather than running out the clock.
        var stuck = Recorded(1e8f);
        Check(stuck.Gears.All(r => DriveSim.TopSpeed(stuck, r).Speed == 0), "nothing moves a 100,000 t vehicle");
        Console.WriteLine($"DRIVE_TESTS_OK: top {top.Speed * 3.6f:0.00} km/h, 0-60 km/h {At(60):0.0} s, heavy {gears[best] * 3.6f:0.0} km/h in gear {best + 1}");
    }
}
