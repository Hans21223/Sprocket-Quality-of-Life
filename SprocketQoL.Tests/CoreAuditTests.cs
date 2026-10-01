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
        Console.WriteLine($"CORE_AUDIT_TESTS_OK: {checks} checks ({oldMisses} old normal failures reproduced)");
    }
}
