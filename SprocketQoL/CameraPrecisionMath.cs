namespace SprocketQoL;

/// A second, designer-only sensitivity multiplier; native player preferences and
/// their interpolation ranges remain unchanged.
public static class CameraPrecisionMath
{
    public static float Scale(float original, float percent, bool designer)
    {
        if (!designer || !float.IsFinite(original)) return original;
        if (!float.IsFinite(percent)) percent = 100;
        return original * (Math.Clamp(percent, 1, 100) / 100);
    }
}
