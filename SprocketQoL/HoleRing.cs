using System.Numerics;

namespace SprocketQoL;

/// Maths for Create Hole, kept free of game types so it can be tested offline: turn the game's hole ring into a
/// true circle that lies in the face, stays inside it, and runs the same way round as the face (so the faces
/// the game fills in around it face outwards like the original face did).
public static class HoleRing
{
    /// New positions for the ring's vertices, in ring order. outer = the face's corners in the face's order,
    /// inner = the ring the game made, centre = where the game put the hole.
    public static Vector3[] Fit(Vector3[] outer, Vector3[] inner, Vector3 centre, out string note)
    {
        int n = outer.Length, m = inner.Length;
        Vector3 Corner(int i) => outer[(i % n + n) % n];

        // Face plane: through the corners' average, facing the way the corners turn.
        var normal = Normal(outer);
        var mid = outer.Aggregate(Vector3.Zero, (s, p) => s + p) / n;
        if (n < 3 || m < 3 || normal.LengthSquared() < 1e-12f) { note = "face too small, left as the game made it"; return inner; }
        normal = Vector3.Normalize(normal);
        Vector3 Flat(Vector3 p) => p - Vector3.Dot(p - mid, normal) * normal;

        // Keep the game's hole position when it's inside the face, otherwise use the face's middle.
        var c = Flat(centre);
        if (!Inside(c)) c = mid;
        float room = Enumerable.Range(0, n).Min(i => DistanceToSegment(c, Flat(Corner(i)), Flat(Corner(i + 1))));
        float asked = inner.Average(p => (Flat(p) - c).Length()); // the game's size, including the Hole size slider
        float radius = Math.Min(asked, room * 0.95f);
        if (radius < 1e-4f) { note = "no room for a hole here, left as the game made it"; return inner; }

        float turn = 0;
        for (int k = 0; k < m; k++) turn += Vector3.Dot(Vector3.Cross(inner[k] - c, inner[(k + 1) % m] - c), normal);

        // Ring vertex k at angle k, turning the same way as the face's corners.
        var u = Flat(Corner(0)) - c;
        if (u.LengthSquared() < 1e-12f) u = Flat(inner[0]) - c;
        u = Vector3.Normalize(u);
        var v = Vector3.Cross(normal, u);
        var ring = new Vector3[m];
        for (int k = 0; k < m; k++)
        {
            double angle = 2 * Math.PI * k / m;
            ring[k] = c + radius * ((float)Math.Cos(angle) * u + (float)Math.Sin(angle) * v);
        }
        note = $"{m} segments, radius {asked * 1000:0} -> {radius * 1000:0} mm, game ring ran {(turn >= 0 ? "same way as" : "opposite to")} the face";
        return ring;

        bool Inside(Vector3 p)
        {
            for (int i = 0; i < n; i++)
                if (Vector3.Dot(Vector3.Cross(Flat(Corner(i + 1)) - Flat(Corner(i)), p - Flat(Corner(i))), normal) < 0) return false;
            return true; // ponytail: convex test, a bent/concave face falls back to its middle
        }
    }

    /// Newell's normal: the side a polygon faces given its corner order (also right for slightly bent faces).
    public static Vector3 Normal(IReadOnlyList<Vector3> corners)
    {
        var normal = Vector3.Zero;
        for (int i = 0; i < corners.Count; i++)
        {
            Vector3 a = corners[i], b = corners[(i + 1) % corners.Count];
            normal += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
        }
        return normal;
    }

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector3.Distance(p, a + t * ab);
    }
}
