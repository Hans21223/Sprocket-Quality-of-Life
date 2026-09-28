using System.Numerics;
using SprocketQoL;

static class GizmoPickingTests
{
    internal static void Run()
    {
        int checks = 0, oldMisses = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
        // Reproduce whole-view cameras, with rings on either side of the selected centre.
        // The former hard-coded 100 m misses both ends of the ring at normal orbit distances.
        foreach (float orbit in new[] { 3f, 10f, 50f })
        foreach (float radius in new[] { 0.1f, 1f, 5f })
        foreach (var direction in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var centre = new Vector3(2, 3, 4);
            var origin = centre - direction * (100 + orbit);
            float reach = GizmoPicking.Reach(100, Vector3.Distance(origin, centre), radius, true);
            foreach (float side in new[] { -1f, 1f })
            {
                var hit = centre + direction * (radius * side);
                float hitDistance = Vector3.Distance(origin, hit);
                if (hitDistance > 100) oldMisses++;
                Check(hitDistance < reach, "orthographic ray must reach the entire ring");
            }
            Check(GizmoPicking.Reach(100, Vector3.Distance(origin, centre), radius, false) == 100,
                "perspective picking must stay unchanged");
        }
        Check(oldMisses > 0, "regression fixtures must fail with the original 100 m limit");
        Check(GizmoPicking.Reach(100, 5, 1, true) == 100, "near gizmos keep their original reach");
        Check(float.IsPositiveInfinity(GizmoPicking.Reach(float.PositiveInfinity, 105, 1, true)), "unlimited stays unlimited");
        Check(GizmoPicking.Reach(100, float.NaN, 1, true) == 100, "invalid geometry keeps original reach");
        Console.WriteLine($"Gizmo picking: {checks} checks passed");
    }
}
