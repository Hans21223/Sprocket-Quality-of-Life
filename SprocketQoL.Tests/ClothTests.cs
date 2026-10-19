using System.Numerics;
using SprocketQoL;

/// The cloth drape: a 2 m sheet dropped on a 1 m box settles on its top, hangs down its sides, never ends up inside it,
/// and keeps its cells close to their size.
static class ClothTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception("cloth: " + message); }

    public static void Run()
    {
        // A closed 1 m box on the ground (y 0 to 1), its faces wound to face out.
        var c = new[] { new Vector3(-.5f, 0, -.5f), new Vector3(.5f, 0, -.5f), new Vector3(.5f, 0, .5f), new Vector3(-.5f, 0, .5f),
                        new Vector3(-.5f, 1, -.5f), new Vector3(.5f, 1, -.5f), new Vector3(.5f, 1, .5f), new Vector3(-.5f, 1, .5f) };
        var quads = new[] { (4, 7, 6, 5), (0, 1, 2, 3), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0) };
        var box = quads.SelectMany(q => new[] { (c[q.Item1], c[q.Item2], c[q.Item3]), (c[q.Item1], c[q.Item3], c[q.Item4]) }).ToList();
        foreach (var (a, b, d) in box)
            Check(Vector3.Dot(Vector3.Cross(b - a, d - a), (a + b + d) / 3 - new Vector3(0, .5f, 0)) > 0, "the test box faces out");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = Cloth.Drape(new Vector3(-1, 1.5f, -1), new Vector3(2, 0, 0), new Vector3(0, 0, 2), 0.1f, box, floor: 0);
        long ms = clock.ElapsedMilliseconds;
        Check(r.Settled, $"settles ({r.Steps} steps)");
        Check(r.Columns == 21 && r.Rows == 21, $"a point every 10 cm ({r.Columns} x {r.Rows})");
        // Flat on the top away from its edges (within two cells of an edge the cloth rounds over it, not through it).
        var top = r.Points.Where(p => Math.Abs(p.X) < .25f && Math.Abs(p.Z) < .25f).ToList();
        Check(top.Count > 0 && top.All(p => Math.Abs(p.Y - 1.015f) < .02f), $"lies on the top ({top.Min(p => p.Y):0.000} to {top.Max(p => p.Y):0.000} m)");
        var outside = r.Points.Where(p => Math.Abs(p.X) > .8f || Math.Abs(p.Z) > .8f).ToList();
        Check(outside.All(p => p.Y < .8f && p.Y >= 0), $"hangs down the sides (highest outside point {outside.Max(p => p.Y):0.00} m)");
        Check(!r.Points.Any(p => Math.Abs(p.X) < .49f && Math.Abs(p.Z) < .49f && p.Y > .01f && p.Y < .99f), "nothing ends up inside the box");
        var sides = new List<float>();
        for (int j = 0; j < r.Rows; j++)
            for (int i = 0; i < r.Columns; i++)
            {
                int k = j * r.Columns + i;
                if (i + 1 < r.Columns) sides.Add(Vector3.Distance(r.Points[k], r.Points[k + 1]) / 0.1f);
                if (j + 1 < r.Rows) sides.Add(Vector3.Distance(r.Points[k], r.Points[k + r.Columns]) / 0.1f);
            }
        // A cell across the box's sharp top edge bridges the corner a little, and one lifted over a corner (so the corner
        // doesn't poke through between points) a little more; the sheet as a whole keeps its size.
        Check(sides.Max() < 1.5f && sides.Average() < 1.03f, $"cells keep their size (most stretched {sides.Max():0.00}x, on average {sides.Average():0.000}x)");

        // A 2 cm pillar, 0.5 m tall, standing between the points of a sheet with points 0.5 m apart: no point lands on it, so
        // only its corners lifting the cloth keep it under the sheet (a periscope or an edge between points).
        var pillar = quads.SelectMany(q => new[] { (P(c[q.Item1]), P(c[q.Item2]), P(c[q.Item3])), (P(c[q.Item1]), P(c[q.Item3]), P(c[q.Item4])) }).ToList();
        static Vector3 P(Vector3 v) => new(0.25f + v.X * 0.02f, v.Y * 0.5f, 0.25f + v.Z * 0.02f);
        var tent = Cloth.Drape(new Vector3(-1, 1, -1), new Vector3(2, 0, 0), new Vector3(0, 0, 2), 0.5f, pillar, floor: 0);
        int cols = tent.Columns;
        var tip = new Vector3(0.25f, 0.5f, 0.25f); // in the middle of the cell from (0, 0) to (0.5, 0.5), on its diagonal
        float over = float.MinValue; // the cloth's height straight over the tip: the triangle whose footprint holds it
        for (int k = 0; k + cols + 1 < tent.Points.Length; k++)
            foreach (var (a, b, d) in k % cols == cols - 1 ? [] : new[] { (k, k + 1, k + cols + 1), (k, k + cols + 1, k + cols) })
            {
                var (pa, e1, e2, q) = (tent.Points[a], tent.Points[b] - tent.Points[a], tent.Points[d] - tent.Points[a], tip - tent.Points[a]);
                float den = e1.X * e2.Z - e2.X * e1.Z, u = (q.X * e2.Z - e2.X * q.Z) / den, v = (e1.X * q.Z - q.X * e1.Z) / den;
                if (u >= 0 && v >= 0 && u + v <= 1) over = Math.Max(over, pa.Y + u * e1.Y + v * e2.Y);
            }
        Check(over >= tip.Y - 0.005f, $"a thin pillar between the points stays under the cloth (cloth {over:0.000} m over its {tip.Y} m tip)");
        Console.WriteLine($"CLOTH_TESTS_OK: settled in {r.Steps} steps, {ms} ms, most stretched {sides.Max():0.00}x, on average {sides.Average():0.000}x; pillar under {over:0.000} m of cloth");
    }
}
