using System.Numerics;
using SprocketQoL;

internal static class RuntimeSafetyTests
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    internal static void Run()
    {
        checks = 0;
        var state = new CanvasScaleState();
        var baseline = new Vector2(1200, 800);
        var first = state.Apply(CanvasScaleState.Mode.ScreenSize, baseline, 1, 150);
        Check(first.Reference == baseline / 1.5f && first.SetReference && !first.SetFactor, "screen-size scaling changes only the reference resolution");
        for (int frame = 0; frame < 50; frame++)
        {
            // Unity recalculates scaleFactor every frame after the reference changes.
            var repeated = state.Apply(CanvasScaleState.Mode.ScreenSize, first.Reference, frame % 2 == 0 ? 1.5f : 2.25f, 150);
            Check(repeated.Reference == first.Reference && !repeated.SetFactor, "calculated canvas scale does not rebase or compound");
        }
        var restored = state.Apply(CanvasScaleState.Mode.ScreenSize, first.Reference, 1.5f, 100);
        Check(restored.Reference == baseline, "100 percent restores the original reference");
        var external = state.Apply(CanvasScaleState.Mode.ScreenSize, new(1600, 900), 1, 200);
        Check(external.Reference == new Vector2(800, 450), "a game-authored resolution update becomes the new baseline");
        Check(state.Apply(CanvasScaleState.Mode.ScreenSize, external.Reference, 2, float.NaN).Reference == new Vector2(1600, 900), "invalid percentage restores a finite native size");
        var fixedWindow = new CanvasScaleState();
        var fit = fixedWindow.Apply(CanvasScaleState.Mode.ScreenSize, new(1100, 760), 1, 200, new(1100, 760));
        Check(fit.Reference == new Vector2(1100, 760), "fixed-size QoL windows keep enough space at 200 percent");
        var small = fixedWindow.Apply(CanvasScaleState.Mode.ScreenSize, fit.Reference, 1, 50, new(1100, 760));
        Check(small.Reference == new Vector2(2200, 1520), "fixed-size windows can still shrink");
        var constant = new CanvasScaleState();
        var constantFirst = constant.Apply(CanvasScaleState.Mode.ConstantPixels, baseline, 1.25f, 200);
        Check(constantFirst.Factor == 2.5f && !constantFirst.SetReference && constantFirst.SetFactor, "constant pixels use the scale factor");
        Check(constant.Apply(CanvasScaleState.Mode.ConstantPixels, baseline, 2.5f, 200).Factor == 2.5f, "constant-pixel scaling does not compound");
        Check(constant.Apply(CanvasScaleState.Mode.ConstantPixels, baseline, 2.5f, 100).Factor == 1.25f, "constant-pixel size restores");
        var physical = new CanvasScaleState().Apply(CanvasScaleState.Mode.Physical, baseline, 2, 200);
        Check(!physical.SetReference && !physical.SetFactor, "physical DPI canvas sizing stays native");
        var switcher = new CanvasScaleState();
        var pixelPlan = switcher.Apply(CanvasScaleState.Mode.ConstantPixels, baseline, 1, 200);
        var screenPlan = switcher.Apply(CanvasScaleState.Mode.ScreenSize, baseline, pixelPlan.Factor, 100);
        Check(screenPlan.SetFactor && screenPlan.Factor == 1, "a mode switch restores a scale factor owned by the old mode");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            Check(CanvasScaleState.Scale(invalid) == 1, "nonfinite scale percentages are neutral");
            var invalidPlan = new CanvasScaleState().Apply(CanvasScaleState.Mode.ScreenSize, new(invalid, 800), 1, 200);
            Check(!invalidPlan.SetReference && !invalidPlan.SetFactor, "invalid canvas dimensions are never written");
        }

        Check(RuntimeMetrics.ValidReload(6, 2, 1, 3), "valid reload timing is accepted");
        foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            Check(!RuntimeMetrics.ValidReload(invalid, 0, 1, 3), "invalid reload totals cannot produce shots per minute");
        Check(!RuntimeMetrics.ValidReload(3, 4, 1, 3) && !RuntimeMetrics.ValidReload(3, 1, -1, 3), "inconsistent reload paths are skipped");
        Check(RuntimeMetrics.TryTrackLength(100, .15, out double length) && Math.Abs(length - 15) < 1e-9, "track length is link count times pitch");
        foreach (double invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
        {
            Check(!RuntimeMetrics.TryTrackLength(invalid, .15, out _), "invalid track link count is refused");
            Check(!RuntimeMetrics.TryTrackLength(100, invalid, out _), "invalid track pitch is refused");
        }
        Console.WriteLine($"RUNTIME_SAFETY_TESTS_OK: {checks} checks passed");
    }
}
