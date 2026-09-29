using SprocketQoL;

/// Standing to top speed, driven the way the game drives: first gear, up a gear each time the engine passes the upshift
/// rpm, held just under the rev limit, no harder than the tracks grip.
static class AccelerationTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception("acceleration: " + message); }

    public static void Run()
    {
        float[] ratios = { 6, 3.5f, 2, 1.2f };
        const float max = 2500, finalDrive = 10, radius = 0.35f, mass = 30000, idle = 600, upshift = 2200;
        float Curve(float rpm) => 2000 * (1 - 0.6f * Math.Min(rpm, max) / max);
        float Resist(float v) => mass * 9.81f * 0.03f + 30 * v * v;
        var engine = Acceleration.RevLimited(Curve, upshift + 50, 0.5f);
        Acceleration.Drivetrain Drive(Func<float, float> torque, float grip = float.MaxValue) =>
            new(torque, ratios, finalDrive, radius, mass, 1.5f, 10, idle, upshift, 0.6f, 0.6f, grip);
        float SpeedAt(float rpm, float ratio) => rpm * MathF.PI / 30 / (ratio * finalDrive) * radius;

        // The rev limiter: full torque below its last 50 rpm, fading to none at the limit, braking past it.
        Check(engine(2100) == Curve(2100) && engine(2249) > 0 && engine(2249) < Curve(2249) * 0.1f && engine(2260) < 0,
              $"rev limiter fades the torque over the last 50 rpm ({engine(2249):0.#} N·m at 2249 rpm)");

        var (seconds, reached, shifts) = Acceleration.Run(Drive(engine), Resist, float.MaxValue);
        Check(shifts == ratios.Length - 1, $"one shift into each higher gear (got {shifts})");
        // Top speed comes from the rev limit (upshift + 50), not the engine's max rpm.
        Check(reached <= SpeedAt(upshift + 50, ratios[^1]) && reached > SpeedAt(upshift, ratios[^1]),
              $"top gear held just under the rev limit ({reached * 3.6f:0.#} km/h; limit {SpeedAt(upshift + 50, ratios[^1]) * 3.6f:0.#})");
        Check(seconds < 120, $"reaches top speed in good time ({seconds:0} s)");

        // Without the limiter the same drive runs on to the max rpm.
        var (_, free, _) = Acceleration.Run(Drive(Acceleration.RevLimited(Curve, max, 0.5f)) with { UpshiftRpm = max - 100 }, Resist, float.MaxValue);
        Check(free > reached, "a higher rev limit goes faster");

        // Less grip than the engine can use: slower off the line, same top speed.
        var (slow, gripped, _) = Acceleration.Run(Drive(engine, mass * 9.81f * 0.3f), Resist, float.MaxValue);
        Check(slow > seconds && MathF.Abs(gripped - reached) < 0.05f, $"grip limits the push, not the top speed ({slow:0} s vs {seconds:0} s)");

        // The tracks' speed limit stops it early.
        var (_, capped, _) = Acceleration.Run(Drive(engine), Resist, 5);
        Check(capped >= 5 * 0.995f && capped < 5.1f, "stops at the tracks' speed limit");

        // Too heavy to move at all: it stops at once rather than running out the clock.
        var (_, still, none) = Acceleration.Run(Drive(engine), v => 1e7f, float.MaxValue);
        Check(still == 0 && none == 0, "can't move: stops at once");

        // A low first gear with real track losses: the game barely expects to lose speed while changing gear, so it
        // shifts up (guessing the real coasting loss, it stayed in first at 5 km/h for ever).
        {
            float[] low = { 7, 4, 2.5f, 1.6f, 1.1f };
            float w = 30000 * 9.81f;
            var tank = new Acceleration.Drivetrain(Acceleration.RevLimited(r => 1600, 2050, 0.5f), low, 8, 0.3f, 30000, 1.5f, 20, 800, 2000, 0.4f, 0.4f, 0.8f * w);
            var (_, got, changes) = Acceleration.Run(tank, v => Acceleration.TrackLosses(v, w, 0.03f, 0.001f, 0, 0, 2, 0.3f), float.MaxValue);
            float topGear = 2050 * MathF.PI / 30 / (1.1f * 8) * 0.3f;
            Check(changes == low.Length - 1 && got > topGear * 0.8f, $"low first gear still shifts up ({changes} shifts, {got * 3.6f:0} km/h)");
        }

        // Track losses: rolling grows with speed, fades in from standing, and sprocket losses add to it.
        float weight = mass * 9.81f;
        Check(Acceleration.TrackLosses(0, weight, 0.03f, 0.001f, 0, 0, 2, radius) == 0, "no rolling resistance standing still");
        Check(MathF.Abs(Acceleration.TrackLosses(10, weight, 0.03f, 0.001f, 0, 0, 2, radius) - 0.03f * weight * 1.1f) < 1,
              "rolling at speed: c0 x weight x (1 + cv v²)");
        Check(Acceleration.TrackLosses(10, weight, 0.03f, 0.001f, 100, 200, 2, radius) > Acceleration.TrackLosses(10, weight, 0.03f, 0.001f, 0, 0, 2, radius),
              "sprocket drag and belt bending add to it");
        Console.WriteLine($"ACCELERATION_TESTS_OK: {shifts} shifts, {reached * 3.6f:0} km/h in {seconds:0} s (grip-limited: {slow:0} s)");
    }
}
