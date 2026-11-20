using SprocketQoL;

static class CameraPrecisionTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new Exception("Camera precision: " + reason); }
        Check(CameraPrecisionMath.Scale(20, 100, true) == 20, "default preserves native sensitivity");
        Check(Math.Abs(CameraPrecisionMath.Scale(20, 1, true) - .2f) < .00001f, "one percent permits fine designer movement");
        Check(CameraPrecisionMath.Scale(20, 25, true) == 5 && CameraPrecisionMath.Scale(-20, 25, true) == -5, "both axis signs use one multiplier");
        foreach (float percent in new[] { -1f, 0f, 1f, 25f, 100f, 1000f, float.NaN, float.PositiveInfinity })
            Check(CameraPrecisionMath.Scale(20, percent, false) == 20, "driving or menu sensitivity is unchanged");
        Check(CameraPrecisionMath.Scale(20, -1, true) == CameraPrecisionMath.Scale(20, 1, true), "below-range values clamp to one percent");
        Check(CameraPrecisionMath.Scale(20, 1000, true) == 20, "above-range values clamp to native speed");
        Check(CameraPrecisionMath.Scale(0, 1, true) == 0, "zero movement remains zero");
        foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
        {
            Check(CameraPrecisionMath.Scale(20, invalid, true) == 20, "invalid configured percentage keeps native speed");
            Check(CameraPrecisionMath.Scale(invalid, 50, true).Equals(invalid), "invalid native value is not manufactured into a new value");
        }
        Check(float.IsFinite(CameraPrecisionMath.Scale(float.MaxValue, 1, true)), "finite sensitivity cannot overflow while reducing it");
        Console.WriteLine($"CAMERA_PRECISION_TESTS_OK: {checks} checks");
    }
}
