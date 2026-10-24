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

        var cubeEdges = faces.SelectMany(Edges).ToHashSet();
        foreach (int segments in new[] { 2, 4, 8, 16 })
        {
            var rounded = EdgeFillet.Round(cube, faces, cubeEdges, .1f, segments);
            Check(rounded.Why == null, "whole cube fillet used by smoothing fixture succeeds");
            var policy = BevelEdges.Smoothing(cube, faces, rounded, cubeEdges, 0);
            Check(policy.OriginalHardEdges.Count == 0, "selected sharp cube edges do not become pinned hard borders");
            Check(policy.HardEdges.Count == 0, "closed rounded cube has no artificial hard corner seams");
            Check(policy.SmoothEdges.Count > cubeEdges.Count, "all edge strips and spherical corners get smooth seams");
            Check(policy.AngleDegrees >= 1 && policy.AngleDegrees < 90,
                "zero degree structure receives enough smoothing without globally accepting right angles");
            var positions = cube.Concat(rounded.Points.Select(p => p.P)).ToArray();
            var output = faces.Where((_, i) => !rounded.Remove.Contains(i)).Concat(rounded.Add.Select(f => f.Corners)).ToArray();
            foreach (var edge in output.SelectMany((f, i) => Edges(f).Select(e => (Edge: e, Face: i))).GroupBy(e => e.Edge))
            {
                var pair = edge.Select(e => e.Face).Distinct().ToArray();
                if (pair.Length != 2 || !policy.SmoothEdges.Contains(edge.Key)) continue;
                Vector3 a = Vector3.Normalize(HoleRing.Normal(output[pair[0]].Select(v => positions[v]).ToList()));
                Vector3 b = Vector3.Normalize(HoleRing.Normal(output[pair[1]].Select(v => positions[v]).ToList()));
                float angle = MathF.Atan2(Vector3.Cross(a,b).Length(), Vector3.Dot(a,b)) * 180 / MathF.PI;
                Check(angle < policy.AngleDegrees, "native strict angle test accepts every marked smooth seam");
            }
            var high = BevelEdges.Smoothing(cube, faces, rounded, cubeEdges, 120);
            Check(high.AngleDegrees == 120, "an existing higher smoothing setting is never lowered");
            Check(high.HardEdges.Count == 0 && high.SmoothEdges.SetEquals(policy.SmoothEdges),
                "surface classification does not depend on the global starting threshold");
            var reordered = BevelEdges.Smoothing(cube, faces,
                rounded with { Add = rounded.Add.AsEnumerable().Reverse().ToList() }, cubeEdges, 0);
            Check(reordered.AngleDegrees == policy.AngleDegrees && reordered.HardEdges.SetEquals(policy.HardEdges) &&
                reordered.SmoothEdges.SetEquals(policy.SmoothEdges), "smoothing classification is independent of face creation order");
        }
        var singleEdge = new HashSet<(int, int)> { (2,3) };
        foreach (bool fillet in new[] { true, false })
        {
            var rounded = fillet ? EdgeFillet.Round(cube, faces, singleEdge, .1f, 4)
                : MeshPlans.Bevel(cube, faces, singleEdge, .1f, 4);
            Check(rounded.Why == null, "partial rounding fixture succeeds");
            var policy = BevelEdges.Smoothing(cube, faces, rounded, singleEdge, 0);
            Check(policy.OriginalHardEdges.SetEquals(cubeEdges.Except(singleEdge)),
                "all original unrounded right angles are pinned before increasing the threshold");
            var sources = BevelEdges.Sources(cube, faces, rounded, singleEdge);
            foreach (var edge in sources.Where(e => policy.OriginalHardEdges.Contains(e.Value)))
                Check(policy.HardEdges.Contains(edge.Key), "rebuilt descendants retain original angle-hard borders");
            Check(policy.SmoothEdges.Count > 0 && !policy.SmoothEdges.Overlaps(policy.HardEdges),
                "rounded seams are smoothed and hard borders never receive conflicting settings");
            Check(policy.HardEdges.Any(e => !policy.OriginalHardEdges.Contains(e) && !sources.ContainsKey(e)),
                "new perpendicular end-cap arc borders are explicitly hard");
            Check(policy.AngleDegrees < 45, "end caps do not inflate the smoothing angle to ninety degrees");
            var high = BevelEdges.Smoothing(cube, faces, rounded, singleEdge, 120);
            Check(high.AngleDegrees == 120 && high.HardEdges.Count > 0,
                "even a high preexisting angle leaves newly created perpendicular end caps explicitly hard");
        }
        float twenty = 20 * MathF.PI / 180;
        var hinge = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY,
            new Vector3(0, -MathF.Cos(twenty), MathF.Sin(twenty)) };
        var hingeFaces = new[] { new[] { 0,1,2 }, new[] { 1,0,3 } };
        var unchanged = new MeshPlans.Rebuild(new(), new(), new(), null);
        Check(BevelEdges.Smoothing(hinge, hingeFaces, unchanged, new HashSet<(int,int)>(), 10)
            .OriginalHardEdges.SetEquals(new[] { (0,1) }), "an unrelated twenty degree edge retains its old ten degree hardness");
        Check(BevelEdges.Smoothing(hinge, hingeFaces, unchanged, new HashSet<(int,int)>(), 30)
            .OriginalHardEdges.Count == 0, "a previously smooth twenty degree edge is not pinned by a thirty degree threshold");
        float shallowAngle = .2f * MathF.PI / 180;
        var shallow = hinge.ToArray();
        shallow[3] = new Vector3(0, -MathF.Cos(shallowAngle), MathF.Sin(shallowAngle));
        Check(BevelEdges.Smoothing(shallow, hingeFaces, unchanged, new HashSet<(int,int)>(), 0)
            .OriginalHardEdges.Contains((0,1)), "disabled normal averaging preserves even a sub-degree hard edge");
        Check(BevelEdges.Smoothing(shallow, hingeFaces, unchanged, new HashSet<(int,int)>(), .5f)
            .OriginalHardEdges.Count == 0, "enabled averaging keeps a previously smooth sub-degree edge smooth");
        var combinedPoints = cube.Concat(hinge.Select(v => v + new Vector3(3,0,0))).ToList();
        var combinedFaces = faces.Concat(hingeFaces.Select(f => f.Select(v => v + cube.Count).ToArray())).ToList();
        var combinedRound = EdgeFillet.Round(combinedPoints, combinedFaces, cubeEdges, .1f, 4);
        Check(combinedRound.Why == null, "rounding a cube beside an untouched shallow hinge succeeds");
        var combinedPolicy = BevelEdges.Smoothing(combinedPoints, combinedFaces, combinedRound, cubeEdges, 10);
        Check(combinedPolicy.AngleDegrees > 20 && combinedPolicy.OriginalHardEdges.Contains((8,9)) &&
            combinedPolicy.HardEdges.Contains((8,9)),
            "raising smoothing for rounded corners pins a disconnected twenty degree edge that was previously hard");

        void SupportedPolicy(IReadOnlyList<Vector3> input, IReadOnlyList<int[]> inputFaces,
            HashSet<(int,int)> selected, float radius, float spacing, int segments)
        {
            var supported = EdgeFillet.Round(input, inputFaces, selected, radius, segments, spacing);
            Check(supported.Why == null, "support-loop edge policy fixture succeeds: " + supported.Why);
            var policy = BevelEdges.Smoothing(input, inputFaces, supported, selected, 0);
            var inherited = BevelEdges.Sources(input, inputFaces, supported, selected);
            var at = input.Concat(supported.Points.Select(p => p.P)).ToArray();
            Vector3 Normal(IEnumerable<int> corners) => Vector3.Normalize(HoleRing.Normal(corners.Select(v => at[v]).ToList()));
            float Angle(Vector3 a, Vector3 b) => MathF.Atan2(Vector3.Cross(a,b).Length(), Vector3.Dot(a,b)) * 180 / MathF.PI;
            var originalNormals = inputFaces.Select(Normal).ToArray();
            var after = inputFaces.Select((f,i) => (Corners: f, Source: i)).Where((_,i) => !supported.Remove.Contains(i))
                .Concat(supported.Add.Select(f => (f.Corners, f.Source))).ToArray();
            var normals = after.Select(f => Normal(f.Corners)).ToArray();
            var curved = after.Select((f,i) => Angle(normals[i],originalNormals[f.Source]) > .05f).ToArray();
            int interior = 0, tangent = 0, endCap = 0;
            foreach (var group in after.SelectMany((f,i) => Edges(f.Corners).Select(e => (Edge: e, Face: i))).GroupBy(e => e.Edge))
            {
                var pair = group.Select(e => e.Face).Distinct().ToArray();
                if (pair.Length != 2) continue;
                int a = pair[0], b = pair[1];
                if (!curved[a] && !curved[b] && after[a].Source == after[b].Source)
                {
                    interior++;
                    Check(!inherited.ContainsKey(group.Key),
                        "an interior coplanar support seam never borrows Sharp or plate-connection flags from an old border");
                    Check(!policy.HardEdges.Contains(group.Key), "an interior support loop never acquires an artificial hard line");
                }
                if (curved[a] == curved[b]) continue;
                int curve = curved[a] ? a : b, flat = curved[a] ? b : a;
                if (Angle(normals[flat], originalNormals[after[curve].Source]) <= .05f)
                {
                    tangent++;
                    Check(policy.SmoothEdges.Contains(group.Key) && !policy.HardEdges.Contains(group.Key),
                        "flat support bands remain smoothly tangent to the rounded profile");
                }
                else
                {
                    endCap++;
                    Check(policy.HardEdges.Contains(group.Key), "support loops preserve the hard perpendicular end-cap boundary");
                }
            }
            Check(interior > 0 && tangent > 0, "fixture exercises actual flat support bands and their tangent seams");
            Check(!policy.HardEdges.Overlaps(policy.SmoothEdges), "support-loop seams never receive conflicting normal settings");
            if (selected.Count == 1) Check(endCap > 0, "partial support-loop fixture covers hard end-cap arcs");
            if (selected.Count == cubeEdges.Count) Check(policy.HardEdges.Count == 0,
                "support loops do not add false hard lines to a fully rounded cube");
        }
        foreach (var size in new[] { (Radius: .1f, Spacing: .005f), (Radius: .03f, Spacing: .001f) })
            foreach (var selected in new[] { singleEdge, new HashSet<(int,int)> { (3,7),(5,7),(6,7) }, cubeEdges })
                SupportedPolicy(cube, faces, selected, size.Radius, size.Spacing, 4);
        var supportRotation = Quaternion.CreateFromYawPitchRoll(.7f, -.3f, .4f);
        var rotatedCube = cube.Select(v => Vector3.Transform(v, supportRotation) + new Vector3(3,-2,5)).ToArray();
        SupportedPolicy(rotatedCube, faces, singleEdge, .03f, .001f, 4);
        Console.WriteLine($"BEVEL_EDGE_TESTS_OK: {checks} checks passed");
    }
}
