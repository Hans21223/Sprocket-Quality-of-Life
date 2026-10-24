using System.Numerics;
using SprocketQoL;

static class HoleRingAlignmentTests
{
    internal static void Run()
    {
        int checks = 0, fits = 0;
        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new Exception(message);
        }

        const float radius = 1.25f, tolerance = 0.00003f;
        // Explicit part-local directions make these fixtures independent of the fitter's basis construction.
        // U is a projected part axis; V completes the positively wound face plane.
        var planes = new[]
        {
            (Name: "horizontal", U: Vector3.UnitZ, V: Vector3.UnitX, N: Vector3.UnitY),
            (Name: "vertical front", U: Vector3.UnitY, V: -Vector3.UnitX, N: Vector3.UnitZ),
            (Name: "vertical side", U: Vector3.UnitY, V: Vector3.UnitZ, N: Vector3.UnitX),
            (Name: "sloped roof", U: Vector3.Normalize(new Vector3(0, 1, -1)),
                V: -Vector3.UnitX, N: Vector3.Normalize(new Vector3(0, 1, 1))),
            (Name: "compound slope", U: Vector3.Normalize(new Vector3(-2, 10, -6)),
                V: Vector3.Normalize(new Vector3(-3, 0, 1)), N: Vector3.Normalize(new Vector3(1, 2, 3)))
        };
        var counts = new[] { 4, 8, 16, 32, 64, 96, 5, 15, 31 };
        var offsets = new[] { Vector3.Zero, new Vector3(37.25f, -18.5f, 90.125f) };

        Vector3[] Outline(Vector3 centre, Vector3 u, Vector3 v) => new[]
        {
            centre - 4 * u - 3 * v,
            centre + 4 * u - 3 * v,
            centre + 4 * u + 3 * v,
            centre - 4 * u + 3 * v
        };
        Vector3[] Seed(int count, Vector3 centre, Vector3 u, Vector3 v, float phase) =>
            Enumerable.Range(0, count).Select(i =>
            {
                float angle = phase + MathF.Tau * i / count;
                return centre + radius * (MathF.Cos(angle) * u + MathF.Sin(angle) * v);
            }).ToArray();
        Vector3[] Fit(Vector3[] face, Vector3[] source, Vector3 centre, string label)
        {
            Check(HoleRing.TryFit(face, source, centre, out var ring, out var note), label + ": accepted (" + note + ")");
            fits++;
            Check(ring.Length == source.Length, label + ": requested segment count preserved");
            return ring;
        }
        void Verify(Vector3[] ring, Vector3 centre, Vector3 u, Vector3 v, Vector3 normal, string label, bool cardinal = true)
        {
            int count = ring.Length;
            var mean = centre + ring.Aggregate(Vector3.Zero, (sum, p) => sum + (p - centre)) / count;
            Check(Vector3.Distance(mean, centre) < tolerance, label + ": circle centre preserved");
            float chord = 2 * radius * MathF.Sin(MathF.PI / count);
            for (int i = 0; i < count; i++)
            {
                var p = ring[i] - centre;
                var next = ring[(i + 1) % count] - centre;
                Check(Math.Abs(p.Length() - radius) < tolerance, label + ": equal radius at " + i);
                Check(Math.Abs(Vector3.Dot(p, normal)) < tolerance, label + ": vertex stays in face plane at " + i);
                Check(Math.Abs(Vector3.Distance(p, next) - chord) < tolerance, label + ": equally spaced neighbours at " + i);
                Check(Vector3.Dot(Vector3.Cross(p, next), normal) > 0, label + ": winding follows face at " + i);
            }
            if (!cardinal) return;
            Check(Vector3.Distance(ring[0], centre + radius * u) < tolerance,
                label + ": starts at a part-local cardinal direction regardless of source phase");
            if (count % 4 != 0) return; // A regular non-multiple of four cannot contain all four cardinals.

            var cardinals = new[] { u, v, -u, -v };
            for (int quarter = 0; quarter < 4; quarter++)
                Check(Vector3.Distance(ring[quarter * count / 4], centre + radius * cardinals[quarter]) < tolerance,
                    label + ": actual vertex at cardinal quarter " + quarter);

            var xy = ring.Select(p => new Vector2(Vector3.Dot(p - centre, u), Vector3.Dot(p - centre, v))).ToArray();
            Check(Math.Abs(xy.Max(p => p.X) - radius) < tolerance && Math.Abs(xy.Min(p => p.X) + radius) < tolerance,
                label + ": both U axis extrema reach the full radius");
            Check(Math.Abs(xy.Max(p => p.Y) - radius) < tolerance && Math.Abs(xy.Min(p => p.Y) + radius) < tolerance,
                label + ": both V axis extrema reach the full radius");
        }

        foreach (var plane in planes)
        foreach (var centre in offsets)
        foreach (bool reversed in new[] { false, true })
        foreach (int count in counts)
        {
            var face = Outline(centre, plane.U, plane.V);
            if (reversed) Array.Reverse(face);
            var v = reversed ? -plane.V : plane.V;
            var normal = reversed ? -plane.N : plane.N;
            string label = $"{plane.Name}, {count} points, translated {centre != Vector3.Zero}, reversed {reversed}";
            Vector3[]? first = null;
            // Neither source phase has a point at the expected cardinal; the fitter must canonicalize it.
            foreach (float phase in new[] { 0.173f, 0.947f })
            {
                var ring = Fit(face, Seed(count, centre, plane.U, plane.V, phase), centre, label);
                Verify(ring, centre, plane.U, v, normal, label);
                if (first != null)
                    Check(first.Zip(ring).All(pair => Vector3.Distance(pair.First, pair.Second) < tolerance),
                        label + ": changing the native ring phase leaves fitted cardinal alignment unchanged");
                first = ring;
            }
        }

        // Retain the optional flat-sided orientation, including the upright square when four points are requested.
        foreach (var plane in planes)
        foreach (int count in new[] { 4, 5, 32, 96 })
        foreach (bool reversed in new[] { false, true })
        {
            var centre = offsets[1];
            var face = Outline(centre, plane.U, plane.V);
            if (reversed) Array.Reverse(face);
            var v = reversed ? -plane.V : plane.V;
            string label = $"optional flat sides: {plane.Name}, {count} points, reversed {reversed}";
            Check(HoleRing.TryFit(face, Seed(count, centre, plane.U, plane.V, 0.317f), centre,
                out var ring, out var note, cardinalPoints: false), label + ": accepted (" + note + ")");
            fits++;
            Check(ring.Length == count, label + ": requested segment count preserved");
            Verify(ring, centre, plane.U, v, reversed ? -plane.N : plane.N, label, cardinal: false);
            float halfStep = MathF.PI / count;
            var expectedStart = centre + radius * (MathF.Cos(halfStep) * plane.U + MathF.Sin(halfStep) * v);
            Check(Vector3.Distance(ring[0], expectedStart) < tolerance, label + ": optional half-segment orientation preserved");
            if (count == 4)
            {
                var xy = ring.Select(p => new Vector2(Vector3.Dot(p - centre, plane.U), Vector3.Dot(p - centre, v))).ToArray();
                float halfSide = radius / MathF.Sqrt(2);
                Check(xy.All(p => Math.Abs(Math.Abs(p.X) - halfSide) < tolerance && Math.Abs(Math.Abs(p.Y) - halfSide) < tolerance),
                    label + ": four-point legacy hole is an upright square");
            }
        }

        // Mirroring a part keeps corresponding ring vertices aligned while winding follows its mirrored face.
        Vector3 Reflect(Vector3 p) => new(-p.X, p.Y, p.Z);
        foreach (var plane in planes)
        foreach (var centre in offsets)
        foreach (bool reversed in new[] { false, true })
        {
            var face = Outline(centre, plane.U, plane.V);
            if (reversed) Array.Reverse(face);
            var source = Seed(32, centre, plane.U, plane.V, 0.391f);
            string label = $"mirrored {plane.Name}, translated {centre != Vector3.Zero}, reversed {reversed}";
            var original = Fit(face, source, centre, label + " original");
            var mirrored = Fit(face.Select(Reflect).ToArray(), source.Select(Reflect).ToArray(), Reflect(centre), label);
            var reflectedNormal = -Reflect(reversed ? -plane.N : plane.N);
            Verify(mirrored, Reflect(centre), Reflect(plane.U), Reflect(reversed ? -plane.V : plane.V), reflectedNormal, label);
            Check(original.Zip(mirrored).All(pair => Vector3.Distance(Reflect(pair.First), pair.Second) < tolerance),
                label + ": mirror twins retain matching cardinal vertices and coherent order");
        }
        Console.WriteLine($"HOLE_RING_ALIGNMENT_TESTS_OK: {checks} checks, {fits} fitted rings");
    }
}
