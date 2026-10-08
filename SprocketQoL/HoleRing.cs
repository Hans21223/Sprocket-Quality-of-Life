using System.Numerics;

namespace SprocketQoL;

/// Maths for Create Hole, kept free of game types so it can be tested offline: turn the game's hole ring into a
/// true circle that lies in the face, stays inside it, and runs the same way round as the face (so the faces
/// the game fills in around it face outwards like the original face did).
public static class HoleRing
{
    /// New positions for the ring's vertices, in ring order. outer = the face's corners in the face's order,
    /// inner = the ring the game made, centre = where the game put the hole, size = the Hole size slider (1 = the
    /// game's size). The size is applied here, never through the game's own hole radius: a hole the game cuts bigger
    /// than its face crashes the game in its own code, before this can shrink it.
    public static Vector3[] Fit(Vector3[] outer, Vector3[] inner, Vector3 centre, out string note, float size = 1f) =>
        TryFit(outer, inner, centre, out var ring, out note, size) ? ring : inner;

    /// A rejected fit has no usable ring. Live callers must check this result before replacing a face;
    /// Fit above retains its historical fallback only for callers that already handle their own failure.
    public static bool TryFit(Vector3[] outer, Vector3[] inner, Vector3 centre, out Vector3[] ring, out string note, float size = 1f)
    {
        ring = Array.Empty<Vector3>();
        int n = outer.Length, m = inner.Length;
        if (n < 3 || m < 3 || outer.Any(p => !Finite(p)) || inner.Any(p => !Finite(p)) || !float.IsFinite(size) || size <= 0)
        { note = "invalid face, ring or relative size"; return false; }

        // Work about the face, not absolute coordinates, so small plates far from the origin remain stable.
        var normal = Normal(outer);
        float normalSq = normal.LengthSquared();
        if (!Finite(normal) || !float.IsFinite(normalSq) || normalSq < 1e-12f)
        { note = "face is too small or degenerate for a hole"; return false; }
        normal = Vector3.Normalize(normal);
        var origin = outer[0];
        var mid = origin + outer.Aggregate(Vector3.Zero, (s, p) => s + (p - origin)) / n;
        Vector3 Flat(Vector3 p) => p - Vector3.Dot(p - mid, normal) * normal;
        var corners = outer.Select(Flat).ToArray();
        var originalRing = inner.Select(Flat).ToArray();
        if (!Finite(mid) || corners.Any(p => !Finite(p)) || originalRing.Any(p => !Finite(p)))
        { note = "face or ring cannot be projected safely"; return false; }
        float extent = corners.Max(p => (p - mid).Length());
        float epsilon = Math.Max(1e-7f, extent * 1e-6f);
        if (!float.IsFinite(extent) || Enumerable.Range(0, n).Any(i => Enumerable.Range(i + 1, n - i - 1)
            .Any(j => Vector3.DistanceSquared(corners[i], corners[j]) <= epsilon * epsilon)))
        { note = "face has repeated or collapsed corners"; return false; }

        // Square to the part; this basis also gives mirror twins the same circle orientation.
        var u = Vector3.Normalize(new[] { Vector3.UnitY, Vector3.UnitZ }.Select(a => a - Vector3.Dot(a, normal) * normal).First(a => a.LengthSquared() > 0.1f));
        var v = Vector3.Cross(normal, u);
        Vector2 InPlane(Vector3 p) => new(Vector3.Dot(p - mid, u), Vector3.Dot(p - mid, v));
        var outline = corners.Select(InPlane).ToArray();
        double Cross(Vector2 a, Vector2 b) => (double)a.X * b.Y - (double)a.Y * b.X;
        bool Crosses(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            double tolerance = epsilon * Math.Max(extent, epsilon);
            double abC = Cross(b - a, c - a), abD = Cross(b - a, d - a), cdA = Cross(d - c, a - c), cdB = Cross(d - c, b - c);
            bool On(Vector2 p, Vector2 x, Vector2 y) => p.X >= Math.Min(x.X, y.X) - epsilon && p.X <= Math.Max(x.X, y.X) + epsilon &&
                p.Y >= Math.Min(x.Y, y.Y) - epsilon && p.Y <= Math.Max(x.Y, y.Y) + epsilon;
            return (Math.Abs(abC) <= tolerance && On(c, a, b)) || (Math.Abs(abD) <= tolerance && On(d, a, b)) ||
                (Math.Abs(cdA) <= tolerance && On(a, c, d)) || (Math.Abs(cdB) <= tolerance && On(b, c, d)) ||
                ((abC > tolerance && abD < -tolerance || abC < -tolerance && abD > tolerance) &&
                 (cdA > tolerance && cdB < -tolerance || cdA < -tolerance && cdB > tolerance));
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (j != (i + 1) % n && i != (j + 1) % n && Crosses(outline[i], outline[(i + 1) % n], outline[j], outline[(j + 1) % n]))
                { note = "face outline crosses itself"; return false; }

        // Measure the source ring about its own centre BEFORE moving the hole. Otherwise a collapsed
        // native circle at an edge could turn into a large requested radius merely by relocating its centre.
        var ringOrigin = originalRing[0];
        var ringCentre = ringOrigin + originalRing.Aggregate(Vector3.Zero, (s, p) => s + (p - ringOrigin)) / m;
        float requested = originalRing.Average(p => (p - ringCentre).Length()) * size;
        float ringArea = Math.Abs(Vector3.Dot(Normal(originalRing), normal));
        if (!float.IsFinite(requested) || requested < 1e-4f || !float.IsFinite(ringArea) || ringArea < 1e-4f * (requested / size) * (requested / size))
        { note = "hole ring is collapsed or too small"; return false; }

        float Room(Vector3 p) => Enumerable.Range(0, n).Min(i => DistanceToSegment(p, corners[i], corners[(i + 1) % n]));
        bool Inside(Vector3 p)
        {
            if (!Finite(p) || Room(p) <= epsilon) return false; // a boundary point has no safe radius
            var q = InPlane(p);
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                var a = outline[i] - q; var b = outline[(i + 1) % n] - q;
                total += Math.Atan2(Cross(a, b), Vector2.Dot(a, b));
            }
            return Math.Abs(total) > Math.PI;
        }
        var c = Finite(centre) ? Flat(centre) : mid;
        bool relocated = !Inside(c);
        if (relocated)
        {
            // The mean may lie outside a concave plate. Test several known positions and retain
            // the interior one with most clearance; never interpret a boundary as a valid centre.
            var candidates = new List<Vector3> { mid };
            for (int i = 0; i < n; i++) candidates.Add((corners[(i + n - 1) % n] + corners[i] + corners[(i + 1) % n]) / 3);
            float left = outline.Min(p => p.X), right = outline.Max(p => p.X), bottom = outline.Min(p => p.Y), top = outline.Max(p => p.Y);
            for (int x = 1; x < 8; x++)
                for (int y = 1; y < 8; y++) candidates.Add(mid + u * (left + (right - left) * x / 8) + v * (bottom + (top - bottom) * y / 8));
            var safe = candidates.Where(Inside).OrderByDescending(Room).ToList();
            if (safe.Count == 0) { note = "no safe interior position for a hole in this face"; return false; }
            c = safe[0];
        }
        float radius = Math.Min(requested, Room(c) * 0.95f);
        if (!float.IsFinite(radius) || radius < 1e-4f)
        { note = "not enough clearance for a hole in this face"; return false; }
        var result = new Vector3[m];
        for (int k = 0; k < m; k++)
        {
            double angle = 2 * Math.PI * (k + 0.5) / m;
            result[k] = c + radius * ((float)Math.Cos(angle) * u + (float)Math.Sin(angle) * v);
        }
        if (result.Any(p => !Inside(p))) { note = "round hole could not stay inside the face"; return false; }
        ring = result;
        note = $"{m} segments, radius {requested * 1000:0} -> {radius * 1000:0} mm" + (relocated ? ", centre moved inside the face" : "");
        return true;
    }

    /// Newell's normal: the side a polygon faces given its corner order (also right for slightly bent faces).
    public static Vector3 Normal(IReadOnlyList<Vector3> corners)
    {
        if (corners.Count < 3) return Vector3.Zero;
        // Work relative to one corner: absolute-coordinate products lose the normal of
        // small faces far from the origin. The signed fan sum equals Newell's normal.
        var origin = corners[0];
        var normal = Vector3.Zero;
        for (int i = 1; i + 1 < corners.Count; i++)
            normal += Vector3.Cross(corners[i] - origin, corners[i + 1] - origin);
        return normal;
    }

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(p, a + t * ab);
    }
}
