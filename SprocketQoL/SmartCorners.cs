using System.Numerics;

namespace SprocketQoL;

/// Smart corners: each corner left on Auto tries the game's own thickening modes, and keeps the one whose inner corner
/// lands nearest where this plate's inner face meets its neighbours'. The modes are saved in the design, so every
/// player sees the same plates (patching Auto itself would change every existing vehicle, and only with QoL).
public static class SmartCorners
{
    /// A plate at the corner: its inner side's direction (unit length) and thickness (metres).
    public readonly record struct Plate(Vector3 Inward, float Thickness);

    /// Where the corner's inner point should be: on this plate's inner plane, and on each neighbour's (across the side to
    /// the next corner, `toNext`, and the side from the previous one, `fromPrev`). A side with no neighbour, or one flat
    /// with this plate, keeps the plate's edge square there instead. Null where those planes don't meet in one point.
    public static Vector3? Ideal(Vector3 corner, Plate self, Plate? next, Vector3 toNext, Plate? prev, Vector3 fromPrev)
    {
        var planes = new List<(Vector3 N, float D)> { (self.Inward, Vector3.Dot(self.Inward, corner) + self.Thickness) };
        void Side(Plate? other, Vector3 along)
        {
            if (other is { } o && MathF.Abs(Vector3.Dot(o.Inward, self.Inward)) < 0.999f)
                planes.Add((o.Inward, Vector3.Dot(o.Inward, corner) + o.Thickness));
            else if (Vector3.Cross(along, self.Inward) is var square && square.LengthSquared() > 1e-12f)
                planes.Add((Vector3.Normalize(square), Vector3.Dot(Vector3.Normalize(square), corner)));
        }
        Side(next, toNext);
        Side(prev, fromPrev);
        if (planes.Count < 3) return null;
        var (a, b, c) = (planes[0], planes[1], planes[2]);
        var bc = Vector3.Cross(b.N, c.N);
        float det = Vector3.Dot(a.N, bc);
        if (MathF.Abs(det) < 1e-4f) return null;
        return (a.D * bc + b.D * Vector3.Cross(c.N, a.N) + c.D * Vector3.Cross(a.N, b.N)) / det;
    }

    /// Which of `options` (where each mode puts the inner corner) to keep: the one nearest `ideal`, as long as it's within
    /// four thicknesses of the corner (further is a spike), and only if it beats option `current` by 0.1 mm.
    public static int Best(Vector3 corner, float thickness, Vector3 ideal, IReadOnlyList<Vector3> options, int current)
    {
        float Score(int i) => !float.IsFinite(options[i].X + options[i].Y + options[i].Z) || Vector3.Distance(options[i], corner) > 4 * thickness
            ? float.PositiveInfinity : Vector3.Distance(options[i], ideal);
        int best = current;
        float bestScore = Score(current);
        for (int i = 0; i < options.Count; i++)
            if (Score(i) < bestScore - 1e-4f) (best, bestScore) = (i, Score(i));
        return best;
    }
}
