using System.Numerics;

namespace SprocketQoL;

/// State kept outside Unity so resizing feedback and failed saves can be regression-tested offline.
internal sealed class CanvasScaleState
{
    internal enum Mode { ConstantPixels, ScreenSize, Physical }
    internal readonly record struct Result(Vector2 Reference, float Factor, bool SetReference, bool SetFactor);
    bool initialized;
    Mode mode;
    Vector2 reference, appliedReference;
    float factor, appliedFactor;

    internal static float Scale(float percent) => float.IsFinite(percent) ? Math.Clamp(percent, 50, 200) / 100 : 1;
    internal Result Apply(Mode currentMode, Vector2 currentReference, float currentFactor, float percent, Vector2 minimumReference = default)
    {
        if (currentMode == Mode.ScreenSize
            && (!float.IsFinite(currentReference.X) || !float.IsFinite(currentReference.Y) || currentReference.X <= 0 || currentReference.Y <= 0)
            || currentMode == Mode.ConstantPixels && (!float.IsFinite(currentFactor) || currentFactor <= 0))
            return new(currentReference, currentFactor, false, false);
        bool restoreReference = false, restoreFactor = false;
        if (initialized && mode != currentMode)
        {
            if (mode == Mode.ScreenSize && currentReference == appliedReference) { currentReference = reference; restoreReference = true; }
            if (mode == Mode.ConstantPixels && currentFactor == appliedFactor) { currentFactor = factor; restoreFactor = true; }
        }
        if (!initialized || mode != currentMode
            || currentMode == Mode.ScreenSize && currentReference != appliedReference
            || currentMode == Mode.ConstantPixels && currentFactor != appliedFactor)
        {
            reference = currentReference; factor = currentFactor;
            mode = currentMode; initialized = true;
        }
        float scale = Scale(percent);
        if (currentMode == Mode.ScreenSize && minimumReference.X > 0 && minimumReference.Y > 0)
            scale = Math.Min(scale, Math.Min(reference.X / minimumReference.X, reference.Y / minimumReference.Y));
        appliedReference = currentMode == Mode.ScreenSize ? reference / scale : currentReference;
        appliedFactor = currentMode == Mode.ConstantPixels ? factor * scale : currentFactor;
        return new(appliedReference, appliedFactor, currentMode == Mode.ScreenSize || restoreReference, currentMode == Mode.ConstantPixels || restoreFactor);
    }
}

internal static class RuntimeMetrics
{
    internal static bool ValidReload(float total, float travel, float distance, float projectile)
        => float.IsFinite(total) && total > 0 && float.IsFinite(travel) && travel >= 0 && travel <= total
           && float.IsFinite(distance) && distance >= 0 && float.IsFinite(projectile) && projectile > 0
           && double.IsFinite(60d / total * projectile);
    internal static bool TryTrackLength(double count, double pitch, out double length)
    {
        length = count * pitch;
        return double.IsFinite(count) && count >= 1 && count == Math.Truncate(count)
            && double.IsFinite(pitch) && pitch > 0 && double.IsFinite(length) && length > 0;
    }
}
