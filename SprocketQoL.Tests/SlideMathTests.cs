using SprocketQoL;

/// SlideMath: what the game's edge slide hands ApplyElem (read from its code), the line-up search, typed amounts.
static class SlideMathTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Slide: " + why); }

        // Clamped and not even: the side follows the amount's sign; otherwise the side it kept. Length only for Even.
        Check(SlideMath.EdgeArgs(0.4f, false, false, true, 1, 2) == (0, 0f), "clamped, forward: side 0, no length");
        Check(SlideMath.EdgeArgs(-0.3f, false, false, true, 0, 2) == (1, 0f), "clamped, backward: side 1");
        Check(SlideMath.EdgeArgs(0.4f, false, false, false, 1, 2).Side == 1, "unclamped: the kept side");
        var even = SlideMath.EdgeArgs(0.4f, true, false, true, 1, 2);
        Check(even.Side == 1 && Math.Abs(even.Length - 0.6f) < 1e-6, $"even: ((-0.4) + 1) / 2 of 2 m is 0.6 m on the kept side ({even})");
        Check(Math.Abs(SlideMath.EdgeArgs(0.4f, true, true, true, 0, 2).Length - 1.4f) < 1e-6, "even, flipped: (0.4 + 1) / 2 of 2 m is 1.4 m");

        // The line-up search, on a path bending where the slide starts (x for negative amounts, then up y).
        (float X, float Y) Path(float f) => f < 0 ? (f, 0) : (0, f);
        float To(float tx, float ty, float lo = -1, float hi = 1) => SlideMath.Nearest(f => { var (x, y) = Path(f); return (x - tx) * (x - tx) + (y - ty) * (y - ty); }, lo, hi);
        Check(Math.Abs(To(0, 0.5f) - 0.5f) < 1e-4, "a target up the path: there");
        Check(Math.Abs(To(-0.7f, 0) + 0.7f) < 1e-4, "a target back along the path: there");
        Check(Math.Abs(To(0.2f, 0.6f) - 0.6f) < 1e-4, "a target off the path: its nearest point, past the bend");
        Check(Math.Abs(To(0, 3) - 1) < 1e-6, "a target past the end: the end (Clamp)");
        Check(Math.Abs(To(0, 3, -3, 3) - 3) < 1e-4, "unclamped: as far as it lines up");

        Check(SlideMath.Typed("25") == 0.25f && SlideMath.Typed("-12.5") == -0.125f && SlideMath.Typed("0.") == 0, "typed percent as a fraction");
        Check(SlideMath.Typed("") == null && SlideMath.Typed("-") == null, "nothing typed yet, or a sign alone: none");
        Console.WriteLine($"SLIDE_MATH_TESTS_OK: {checks} checks");
    }
}
