using SprocketQoL;

/// Standing to top speed: an engine whose torque falls with the revs has gears that cross over, where losing speed while
/// changing gear used to make the lower gear look better again, so it changed back and forth for ever (seen in game:
/// "0 to 3 km/h in about 600 s (796 shifts)"). Now each gear once, up to top speed.
static class AccelerationTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception("acceleration: " + message); }

    public static void Run()
    {
        float[] ratios = { 6, 3.5f, 2, 1.2f };
        const float max = 2500, finalDrive = 10, radius = 0.35f, mass = 30000;
        float Torque(float rpm) => 2000 * (1 - 0.6f * Math.Min(rpm, max) / max);
        float Resist(float v) => mass * 9.81f * 0.03f + 30 * v * v;
        float top = max * MathF.PI / 30 / (ratios.Min() * finalDrive) * radius;
        var (seconds, reached, shifts) = Acceleration.Run(Torque, Resist, ratios, finalDrive, radius, mass, 1.5f, 10, max, 1.2f, top);
        Check(shifts == ratios.Length - 1, $"one shift into each higher gear (got {shifts})");
        Check(reached >= top * 0.99f && seconds < 120, $"reaches top speed {top * 3.6f:0.#} km/h in good time (got {reached * 3.6f:0.#} km/h in {seconds:0} s)");
        // Too heavy to move at all: it stops at once rather than running out the clock.
        var (_, still, none) = Acceleration.Run(Torque, v => 1e7f, ratios, finalDrive, radius, mass, 1.5f, 10, max, 1.2f, top);
        Check(still == 0 && none == 0, "can't move: stops at once");
        Console.WriteLine($"ACCELERATION_TESTS_OK: {shifts} shifts, {reached * 3.6f:0} km/h in {seconds:0} s");
    }
}
