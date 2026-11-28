namespace SprocketQoL;

/// The plain maths around the game's edge and vertex slide (SlideAssist), tested offline.
public static class SlideMath
{
    /// What the game's ApplyEdgeSlide hands its ApplyElem besides the amount `fac` (read from its code, Sprocket 0.2.57;
    /// Blender's edge slide): the side, worked out again from the amount when clamped and not even (else the one it
    /// kept), and for Even the distance along the active point's edge: ((flip ? fac : -fac) + 1) / 2 of its length.
    public static (int Side, float Length) EdgeArgs(float fac, bool even, bool flip, bool clamp, int keptSide, float activeEdgeLength)
    {
        int side = !even && clamp ? (fac < 0 ? 1 : 0) : keptSide;
        float length = even ? ((flip ? fac : -fac) + 1) * 0.5f * activeEdgeLength : 0;
        return (side, length);
    }

    /// The amount from `lo` to `hi` where `distance(amount)` is least: 81 samples, then narrowed round the best (a slide
    /// path bends at the point it starts from, so one smooth search could stop on the wrong side of the bend).
    public static float Nearest(Func<float, float> distance, float lo, float hi)
    {
        float best = lo, bestD = float.MaxValue;
        for (int i = 0; i <= 80; i++)
        {
            float f = lo + (hi - lo) * i / 80;
            if (distance(f) is float d && d < bestD) { bestD = d; best = f; }
        }
        float step = (hi - lo) / 80;
        for (int k = 0; k < 20; k++)
        {
            float a = Math.Max(lo, best - step), b = Math.Min(hi, best + step);
            foreach (float f in new[] { a, (a + best) / 2, (best + b) / 2, b })
                if (distance(f) is float d && d < bestD) { bestD = d; best = f; }
            step /= 2;
        }
        return best;
    }

    /// A typed slide amount in percent as a fraction, or null while there's no number yet ("", "-", ".").
    public static float? Typed(string typed) =>
        float.TryParse(typed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float percent) ? percent / 100 : null;
}
