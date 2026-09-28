using System.Numerics;
using SprocketQoL;

static class BevelEdgeTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception(reason); }
        static IEnumerable<(int, int)> Edges(int[] f) => f.Select((v, i) => FaceMerge.Key(v, f[(i + 1) % f.Length]));
        var cube = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)).ToList();
        var faces = new List<int[]> { new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 },
            new[] { 1, 3, 7, 5 }, new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 } };
        faces = faces.Select(f => Vector3.Dot(HoleRing.Normal(f.Select(v => cube[v]).ToList()),
            f.Aggregate(Vector3.Zero, (p, v) => p + cube[v]) / 4 - new Vector3(0.5f)) < 0 ? f.Reverse().ToArray() : f).ToList();
        foreach (var selected in new[] { new[] { (2, 3) }, new[] { (2, 3), (3, 7), (7, 6), (6, 2) },
            new[] { (3, 7), (5, 7), (6, 7) } })
        {
            var plan = MeshPlans.Bevel(cube, faces, selected, 0.1f);
            Check(plan.Why == null && MeshPlans.Check(cube, faces, plan) == null, "valid bevel topology");
            var map = BevelEdges.Sources(cube, faces, plan);
            var old = faces.SelectMany(Edges).ToHashSet();
            var newEdges = plan.Add.SelectMany(f => Edges(f.Corners)).Distinct().ToArray();
            var reversed = BevelEdges.Sources(cube, faces, plan with { Add = plan.Add.AsEnumerable().Reverse().ToList() });
            Check(map.Count == reversed.Count && map.All(e => reversed[e.Key] == e.Value), "settings independent of face build order");
            int Origin(int v) => v < cube.Count ? v : plan.Points[v - cube.Count].Blend.Single().V;
            foreach (var edge in newEdges)
            {
                if (old.Contains(edge)) Check(map[edge] == edge, "unchanged surrounding edges keep their own source");
                else if (Origin(edge.Item1) != Origin(edge.Item2))
                {
                    var original = FaceMerge.Key(Origin(edge.Item1), Origin(edge.Item2));
                    if (old.Contains(original)) Check(map.GetValueOrDefault(edge) == original, "shortened neighbouring side keeps its settings");
                    else Check(!map.ContainsKey(edge), "new diagonal does not borrow unrelated edge settings");
                }
                else Check(!map.ContainsKey(edge), "new chamfer cross-edge has no arbitrary prototype");
            }
            Check(map.Keys.Any(e => e.Item1 >= cube.Count || e.Item2 >= cube.Count), "fixture covers recreated sides");
        }
        // A bevel can end in a triangulated panel, retaining a corner. Both ends of one
        // shortened side then have the same origin; it must still inherit that side's settings.
        var panel = new List<Vector3> { new(0,0,0), new(1,0,0), new(0,1,0) };
        var panelFaces = new List<int[]> { new[] { 0,1,2 } };
        var split = new MeshPlans.Rebuild(new() { 0 }, new() { new(new[] { 0,3,4 }, 0), new(new[] { 3,1,2,4 }, 0) },
            new() { new(new(0.2f,0,0), new[] { (0,1f) }), new(new(0,0.2f,0), new[] { (0,1f) }) }, null);
        var splitMap = BevelEdges.Sources(panel, panelFaces, split);
        Check(splitMap[(0,3)] == (0,1), "retained corner to new point follows horizontal side");
        Check(splitMap[(0,4)] == (0,2), "retained corner to new point follows vertical side");
        Check(!splitMap.ContainsKey((3,4)), "cross-corner edge does not inherit a perimeter setting");
        Console.WriteLine($"BEVEL_EDGE_TESTS_OK: {checks} checks passed");
    }
}
