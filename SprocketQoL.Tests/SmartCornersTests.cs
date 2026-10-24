using System.Numerics;
using SprocketQoL;

/// Smart corners: where a corner's inside should be, and which of the game's modes gets nearest it.
static class SmartCornersTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Smart corners: " + why); }
        const float t = 0.01f;
        bool Near(Vector3? a, Vector3 b) => a is { } p && Vector3.Distance(p, b) < 1e-5f;

        // The corner of a closed box (three plates, insides +x, +y, +z): the three inner faces meet at (t, t, t).
        var box = SmartCorners.Ideal(Vector3.Zero, new(Vector3.UnitZ, t), new(Vector3.UnitX, t), Vector3.UnitX, new(Vector3.UnitY, t), -Vector3.UnitY);
        Check(Near(box, new(t, t, t)), "a box corner's inside is where the three inner faces meet");
        // The game's modes at that corner for the bottom plate: straight in, along the point's normal, along the upright edge.
        var options = new[] { new Vector3(0, 0, t), new Vector3(0, 0, t), new Vector3(t, t, t), new Vector3(0, 0, t) };
        Check(SmartCorners.Best(Vector3.Zero, t, box!.Value, options, 0) == 2, "at a box corner, thickening along the point meets all three plates");

        // A lone plate (no neighbours): its edges stay square, so the inside is straight in.
        var lone = SmartCorners.Ideal(Vector3.Zero, new(Vector3.UnitZ, t), null, Vector3.UnitX, null, -Vector3.UnitY);
        Check(Near(lone, new(0, 0, t)), "a lone plate's corner goes straight in");
        // A plate continuing flat into its neighbour counts as having none there.
        var flat = SmartCorners.Ideal(Vector3.Zero, new(Vector3.UnitZ, t), new(Vector3.UnitZ, t), Vector3.UnitX, null, -Vector3.UnitY);
        Check(Near(flat, new(0, 0, t)), "a flat neighbour keeps the corner straight in");
        // One wall at a right angle (an L joint): the inside lands on the wall's inner face, not short of it (a gap) or past it.
        var joint = SmartCorners.Ideal(Vector3.Zero, new(Vector3.UnitZ, t), new(Vector3.UnitX, 2 * t), Vector3.UnitY, null, Vector3.UnitX);
        Check(Near(joint, new(2 * t, 0, t)), "at an L joint the corner meets the wall's inner face");

        // A sharp corner where the planes meet far away: a spike is refused, Auto kept.
        var spike = new[] { new Vector3(0, 0, t), new Vector3(0, 0, 5 * t) };
        Check(SmartCorners.Best(Vector3.Zero, t, new(0, 0, 5 * t), spike, 0) == 0, "a mode that would stick out more than four thicknesses is refused");
        // No change for less than 0.1 mm better, and none for a mode that isn't a number.
        Check(SmartCorners.Best(Vector3.Zero, t, new(0, 0, t), new[] { new Vector3(0, 0, t + 0.00005f), new Vector3(0, 0, t) }, 0) == 0, "a tiny gain doesn't change a corner");
        Check(SmartCorners.Best(Vector3.Zero, t, new(0, 0, t), new[] { new Vector3(0, 0, 2 * t), new Vector3(float.NaN, 0, 0) }, 0) == 0, "an invalid position is never picked");
        Check(SmartCorners.Ideal(Vector3.Zero, new(Vector3.UnitZ, t), new(Vector3.UnitZ * -1, t), Vector3.UnitX, new(Vector3.UnitZ, t), Vector3.UnitX) is null,
            "planes that don't meet in a point give no ideal (the corner is left alone)");
        Console.WriteLine($"SMART_CORNERS_TESTS_OK: {checks} checks");
    }
}
