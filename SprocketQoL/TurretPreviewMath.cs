using System.Numerics;

namespace SprocketQoL;

/// The turret preview's maths (TurretPreview draws it), in the drawing sheet's turning sense (DrawingOptions.Motion):
/// a positive turret angle turns right, positive elevation raises the muzzle.
public static class TurretPreviewMath
{
    /// `p` turned `degrees` about the line through `centre` along `axis`.
    public static Vector3 Turn(Vector3 p, Vector3 centre, Vector3 axis, float degrees)
        => centre + Vector3.Transform(p - centre, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), degrees * MathF.PI / 180));

    /// Angles from `from` to `to` about `step` degrees apart, both ends included; more than a full turn is one turn.
    public static float[] Angles(float from, float to, float step)
    {
        if (!float.IsFinite(from) || !float.IsFinite(to) || to < from || !(step > 0)) return Array.Empty<float>();
        float span = Math.Min(360, to - from);
        int n = Math.Max(1, (int)MathF.Ceiling(span / step));
        return Enumerable.Range(0, n + 1).Select(i => from + span * i / n).ToArray();
    }

    /// The turret turns all the way round: no limit lines to draw.
    public static bool Full(float min, float max) => max - min >= 359.9f;

    /// How far along `from` → `to` (0 to 1) the line first gets `radius` away from the axis line (`centre`, `axis`):
    /// the stretch of a barrel above its own turret ring is left out of the hull check. 0 when it starts outside,
    /// 1 when it never gets out.
    public static float Outside(Vector3 from, Vector3 to, Vector3 centre, Vector3 axis, float radius)
    {
        axis = Vector3.Normalize(axis);
        Vector3 Flat(Vector3 p) { var d = p - centre; return d - axis * Vector3.Dot(d, axis); }
        var a = Flat(from);
        var b = Flat(to) - a;
        float c = a.LengthSquared() - radius * radius;
        if (c >= 0) return 0;
        float qa = b.LengthSquared(), qb = 2 * Vector3.Dot(a, b);
        if (qa < 1e-12f) return 1;
        float t = (-qb + MathF.Sqrt(qb * qb - 4 * qa * c)) / (2 * qa); // c < 0: always two roots, one either side of 0
        return Math.Clamp(t, 0, 1);
    }
}
