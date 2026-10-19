namespace SprocketQoL;

/// QoL's Smooth for the drive gears: first and top gear stay, and the gears between them go where DriveSim gets the vehicle
/// to 90% of its top speed soonest. The game's own Smooth (TransmissionRules.ComputeIdealRatios) spaces them in equal
/// steps; the spacings tried run from that to steps that shrink towards top gear. Pure maths, no game types.
public static class GearSpacing
{
    /// 1 is the game's equal steps; above 1 the steps shrink towards top gear, below 1 they grow.
    static readonly float[] Exponents = { 0.8f, 1, 1.25f, 1.5f, 1.75f, 2, 2.5f, 3 };

    /// `count` (3 or more) gears from `first` to `top`, their log ratio spaced by 1 - (1 - t)^p over t = 0..1.
    public static float[] Spread(float first, float top, int count, float p)
    {
        var gears = new float[count];
        for (int i = 0; i < count; i++)
            gears[i] = first * MathF.Pow(top / first, 1 - MathF.Pow(1 - i / (float)(count - 1), p));
        gears[^1] = top;
        return gears;
    }

    /// The game's resulting drive ratios (TransmissionBlueprint.GetRatios): with an auxiliary gear each gear also runs at
    /// its ratio × the auxiliary ratio, and all of them are sorted from the lowest gear up.
    public static float[] Resulting(float[] gears, float auxiliary) => MathF.Abs(auxiliary - 1) < 1e-5f ? gears.ToArray()
        : gears.SelectMany(g => new[] { g, g * auxiliary }).OrderByDescending(g => g).ToArray();

    /// The spacing found (Exponent 1 = the game's equal steps) and its time from a standstill to Target (m/s), against the
    /// equal steps' time.
    public readonly record struct Choice(float[] Gears, float Exponent, float Seconds, float EvenSeconds, float Target);

    /// The spacing that reaches 90% of the vehicle's top speed with equal steps soonest; `vehicle`'s own gears are replaced
    /// by each spacing's resulting ratios. Equal steps unless another spacing is at least 1% quicker. Null if the vehicle
    /// can't get moving with equal steps.
    public static Choice? Fastest(DriveSim.Vehicle vehicle, float first, float top, int count, float auxiliary)
    {
        DriveSim.Vehicle With(float p) => vehicle with { Gears = Resulting(Spread(first, top, count, p), auxiliary) };
        var even = With(1);
        float target = 0.9f * even.Gears.Max(r => DriveSim.TopSpeed(even, r).Speed);
        if (target <= 0) return null;
        var seconds = new float[Exponents.Length];
        Parallel.For(0, Exponents.Length, k => seconds[k] = TimeTo(With(Exponents[k]), target));
        int equal = Array.IndexOf(Exponents, 1f), best = Array.IndexOf(seconds, seconds.Min());
        if (float.IsPositiveInfinity(seconds[equal])) return null;
        if (seconds[best] > 0.99f * seconds[equal]) best = equal;
        return new(Spread(first, top, count, Exponents[best]), Exponents[best], seconds[best], seconds[equal], target);
    }

    /// Seconds from a standstill to `speed` (m/s) at full throttle, or +∞ if the vehicle never gets there.
    public static float TimeTo(DriveSim.Vehicle vehicle, float speed)
    {
        var run = DriveSim.Accelerate(vehicle, stopAt: speed);
        return run.Count > 0 && run[^1].Speed >= speed ? run[^1].Time : float.PositiveInfinity;
    }
}
