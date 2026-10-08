using System.Numerics;
using SprocketQoL;

static class CoreAuditTests
{
    internal static void Run()
    {
        int checks = 0, oldMisses = 0;
        void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        Vector3 LegacyNormal(IReadOnlyList<Vector3> points)
        {
            var n = Vector3.Zero;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
            }
            return n;
        }
        Vector3 ReferenceNormal(IReadOnlyList<Vector3> points)
        {
            // Double-precision Newell sum is independent of the new float fan implementation.
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var a = points[i]; var b = points[(i + 1) % points.Count];
                x += ((double)a.Y - b.Y) * ((double)a.Z + b.Z);
                y += ((double)a.Z - b.Z) * ((double)a.X + b.X);
                z += ((double)a.X - b.X) * ((double)a.Y + b.Y);
            }
            return new Vector3((float)x, (float)y, (float)z);
        }
        foreach (float distance in new[] { 0f, 100f, 10000f, 100000f })
        foreach (float size in new[] { .03125f, .125f, .5f, 1f })
        foreach (float angle in new[] { .1f, .7f, 1.2f })
        {
            var place = Matrix4x4.CreateFromYawPitchRoll(angle, angle * .3f, -angle * .7f)
                      * Matrix4x4.CreateTranslation(distance, distance * .3f, -distance * .7f);
            var points = new[] { Vector3.Zero, new Vector3(size, 0, 0), new Vector3(size, size, 0), new Vector3(0, size, 0) }
                .Select(p => Vector3.Transform(p, place)).ToArray();
            var expected = ReferenceNormal(points);
            float tolerance = Math.Max(1e-9f, expected.Length() * 2e-5f);
            if (Vector3.Distance(LegacyNormal(points), expected) > tolerance) oldMisses++;
            Check(Vector3.Distance(HoleRing.Normal(points), expected) <= tolerance, "small translated face keeps its signed normal");
            Check(Vector3.Distance(HoleRing.Normal(points.Reverse().ToArray()), -expected) <= tolerance, "reversed face keeps the opposite normal");
        }
        Check(oldMisses > 0, "normal fixtures reproduce absolute-coordinate cancellation");
        Check(HoleRing.Normal(Array.Empty<Vector3>()) == Vector3.Zero, "empty normal is harmless");
        var face = new[] { Vector3.Zero, Vector3.UnitX, Vector3.One - Vector3.UnitZ, Vector3.UnitY };
        var ring = Enumerable.Range(0, 16).Select(k => new Vector3(.5f + .2f * MathF.Cos(k * MathF.Tau / 16), .5f + .2f * MathF.Sin(k * MathF.Tau / 16), .03f)).ToArray();
        var fitted = HoleRing.Fit(face, ring, new Vector3(float.NaN, 0, 0), out _);
        Check(fitted.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y) && p.Z == 0), "invalid centre uses a safe middle");
        Check(fitted.All(p => Math.Abs(Vector3.Distance(p, new Vector3(.5f, .5f, 0)) - .2f) < 1e-6f), "fallback centre preserves requested radius");
        Check(ReferenceEquals(HoleRing.Fit(Array.Empty<Vector3>(), ring, Vector3.Zero, out _), ring), "empty face leaves the ring untouched");
        var invalidFace = face.ToArray(); invalidFace[1] = new Vector3(float.NaN, 0, 0);
        Check(ReferenceEquals(HoleRing.Fit(invalidFace, ring, Vector3.Zero, out _), ring), "invalid face does not generate NaN vertices");
        var invalidRing = ring.ToArray(); invalidRing[0] = new Vector3(float.PositiveInfinity, 0, 0);
        Check(ReferenceEquals(HoleRing.Fit(face, invalidRing, Vector3.Zero, out _), invalidRing), "invalid ring is not rebuilt");
        Check(ReferenceEquals(HoleRing.Fit(face, Array.Empty<Vector3>(), Vector3.Zero, out _), Array.Empty<Vector3>()), "empty ring is harmless");

        Vector3[] Circle(Vector3 at, float radius) => Enumerable.Range(0, 32).Select(k =>
            at + radius * new Vector3(MathF.Cos(k * MathF.Tau / 32), MathF.Sin(k * MathF.Tau / 32), 0)).ToArray();
        void Reject(Vector3[] outline, Vector3[] source, Vector3 at, string why, float relative = 1)
        {
            var before = source.ToArray();
            Check(!HoleRing.TryFit(outline, source, at, out var result, out _, relative), why);
            Check(result.Length == 0, why + ": rejection has no usable ring");
            Check(source.SequenceEqual(before), why + ": source vertices are untouched");
        }
        var centre = new Vector3(.5f, .5f, 0);
        Reject(face, Enumerable.Repeat(centre, 32).ToArray(), centre, "collapsed native circle is rejected");
        Reject(face, Enumerable.Repeat(Vector3.Zero, 32).ToArray(), Vector3.Zero,
            "collapsed circle at a boundary cannot gain radius when its centre moves");
        Reject(face, Enumerable.Range(0, 32).Select(i => new Vector3(i / 31f, .5f, 0)).ToArray(), centre,
            "a collinear source ring is rejected");
        Reject(new[] { Vector3.Zero, Vector3.UnitX, new Vector3(2, 0, 0) }, Circle(centre, .2f), centre,
            "a collinear face is rejected");
        Reject(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitX, Vector3.UnitY }, Circle(centre, .2f), centre,
            "repeated face corners are rejected");
        Reject(new[] { Vector3.Zero, new Vector3(2, 2, 0), new Vector3(0, 2, 0), Vector3.UnitX }, Circle(centre, .2f), centre,
            "an asymmetric crossing outline is rejected even with a nonzero normal");
        Reject(invalidFace, Circle(centre, .2f), centre, "nonfinite face is rejected by the safe contract");
        Reject(face, invalidRing, centre, "nonfinite ring is rejected by the safe contract");
        Reject(face, Circle(centre, .00001f), centre, "a subminimum source circle is rejected");
        Reject(face, Circle(centre, .2f), centre, "a nonfinite relative size is rejected", float.NaN);
        Reject(face, Circle(centre, .2f), centre, "a zero relative size is rejected", 0);

        var boundaryCentre = new Vector3(.5f, 0, 0);
        Check(HoleRing.TryFit(face, Circle(boundaryCentre, .2f), boundaryCentre, out var movedRing, out var moveNote),
            "a valid ring requested at a boundary moves to a verified interior position");
        var movedCentre = movedRing.Aggregate(Vector3.Zero, (sum, p) => sum + p) / movedRing.Length;
        Check(moveNote.Contains("centre moved") && Vector3.Distance(movedCentre, centre) < 1e-6f,
            "the boundary fallback is the safe middle of the square");
        Check(movedRing.All(p => Math.Abs(Vector3.Distance(p, movedCentre) - .2f) < 1e-6f),
            "moving a boundary centre does not inflate the requested radius");
        Check(HoleRing.TryFit(face, Circle(centre, .2f), new Vector3(float.NaN), out var finiteRing, out _) && finiteRing.All(p => float.IsFinite(p.X)),
            "a nonfinite centre still uses a verified safe interior position");
        var concave = new[] { Vector3.Zero, new Vector3(2, 0, 0), new Vector3(.2f, .2f, 0), new Vector3(0, 2, 0) };
        var concaveMean = concave.Aggregate(Vector3.Zero, (sum, p) => sum + p) / concave.Length;
        Check(HoleRing.TryFit(concave, Circle(concaveMean, .05f), concaveMean, out var concaveRing, out _),
            "a concave face with its mean outside still finds a real interior position");
        Check(concaveRing.All(p => p.X >= 0 && p.Y >= 0 && (p.X <= .2f || p.Y <= .2f)),
            "the concave fallback circle stays inside the authored outline");

        foreach (bool reflected in new[] { false, true })
        foreach (bool reversed in new[] { false, true })
        {
            Vector3 Place(Vector3 p) => reflected ? new Vector3(-p.X, p.Y, p.Z) : p;
            var outline = face.Select(Place).ToArray();
            if (reversed) Array.Reverse(outline);
            var requestedRing = Circle(centre, .2f).Select(Place).ToArray();
            Check(HoleRing.TryFit(outline, requestedRing, Place(centre), out var goodRing, out _, 1),
                "32-segment 100% ring remains valid under mirror and reversed winding");
            var normal = Vector3.Normalize(HoleRing.Normal(outline));
            Check(Vector3.Dot(HoleRing.Normal(goodRing), normal) > 0,
                "safe ring follows the face winding after mirror/reversal");
            var positions = outline.Concat(goodRing).ToList();
            var hole = Enumerable.Range(outline.Length, goodRing.Length).ToList();
            var outlineIds = Enumerable.Range(0, outline.Length).ToList();
            var fill = Fill.Region(positions, outlineIds, new() { hole }, normal, null);
            double Area(IReadOnlyList<int> f) => Vector3.Dot(HoleRing.Normal(f.Select(i => positions[i]).ToArray()), normal) / 2;
            double wanted = Area(outlineIds) - Area(hole);
            Check(fill.Count > 0 && fill.All(f => f.Length is 3 or 4 && Area(f) > 0),
                "fewest-points hole fill produces outward triangles and quads");
            Check(Math.Abs(fill.Sum(Area) - wanted) < 1e-6,
                "fewest-points fill covers the plate minus its real ring exactly once");
        }
        Console.WriteLine($"CORE_AUDIT_TESTS_OK: {checks} checks ({oldMisses} old normal failures reproduced)");
    }
}
