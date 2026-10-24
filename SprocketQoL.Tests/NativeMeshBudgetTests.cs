using System.Numerics;
using SprocketQoL;

static class NativeMeshBudgetTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception(reason); }
        static IEnumerable<(int,int)> Edges(int[] face) => face.Select((v,i) => FaceMerge.Key(v,face[(i+1)%face.Length]));
        static (List<Vector3> Points, List<int[]> Faces) Cubes(int count)
        {
            var points = new List<Vector3>();
            var faces = new List<int[]>();
            int[][] outward = { new[] { 0,1,5,4 },new[] { 6,7,3,2 },new[] { 4,6,2,0 },
                new[] { 1,3,7,5 },new[] { 2,3,1,0 },new[] { 4,5,7,6 } };
            for (int c=0;c<count;c++)
            {
                points.AddRange(Enumerable.Range(0,8).Select(v => new Vector3((v&1)+3*c,(v>>1)&1,(v>>2)&1)));
                faces.AddRange(outward.Select(f => f.Select(v => v+8*c).ToArray()));
            }
            return (points,faces);
        }
        foreach (int count in new[] { 1,8 })
        {
            var (points,faces) = Cubes(count);
            var originalPoints = points.ToArray();
            var originalFaces = faces.Select(f => f.ToArray()).ToArray();
            var edges = faces.SelectMany(Edges).ToHashSet();
            var plan = EdgeFillet.Round(points,faces,edges,.03f,16,.001f);
            Check(plan.Why == null && MeshPlans.Check(points,faces,plan) == null,
                "native budget fixture is valid controlled fillet geometry");
            var beforePlan = (Remove: plan.Remove.ToArray(),
                Faces: plan.Add.Select(f => (Corners: f.Corners.ToArray(), f.Source, f.ReferenceArea)).ToArray(),
                Points: plan.Points.Select(p => (p.P, Blend: p.Blend.ToArray())).ToArray());
            int oldLoops = faces.Sum(f => f.Length);
            var problem = MeshPlans.CreationLimit(points.Count,edges.Count,faces.Count,oldLoops,faces,Array.Empty<(int,int)>(),plan);
            if (count == 1) Check(problem == null, "one fully controlled sixteen-segment cube fits native element indices");
            else
            {
                long newLoops = plan.Add.Sum(f => (long)f.Corners.Length);
                Check(newLoops > ushort.MaxValue && plan.Add.Count < ushort.MaxValue && plan.Points.Count < ushort.MaxValue,
                    "eight small cubes specifically overflow corner loops before points or faces");
                Check(problem != null && problem.Contains("face corners") && problem.Contains((newLoops+oldLoops).ToString("N0",System.Globalization.CultureInfo.InvariantCulture)),
                    "corner budget reports the peak old plus newly created loops before a native mutation");
            }
            Check(points.SequenceEqual(originalPoints) && faces.Zip(originalFaces).All(f => f.First.SequenceEqual(f.Second)),
                "native budget validation never changes original positions or faces");
            Check(plan.Remove.SequenceEqual(beforePlan.Remove) && plan.Add.Count == beforePlan.Faces.Length &&
                plan.Add.Zip(beforePlan.Faces).All(f => f.First.Source == f.Second.Source && f.First.ReferenceArea == f.Second.ReferenceArea &&
                    f.First.Corners.SequenceEqual(f.Second.Corners)) && plan.Points.Count == beforePlan.Points.Length &&
                plan.Points.Zip(beforePlan.Points).All(p => p.First.P == p.Second.P && p.First.Blend.SequenceEqual(p.Second.Blend)),
                "native budget validation never changes the prepared fillet plan");
        }

        var triangle = new[] { new[] { 0,1,2 } };
        var replacement = new MeshPlans.Rebuild(new() { 0 }, new() { new(new[] { 0,1,2 },0) }, new(),null);
        Check(MeshPlans.CreationLimit(3,3,1,ushort.MaxValue-3,triangle,Array.Empty<(int,int)>(),replacement) == null,
            "the exact corner index capacity remains usable");
        Check(MeshPlans.CreationLimit(3,3,1,ushort.MaxValue-2,triangle,Array.Empty<(int,int)>(),replacement)?.Contains("face corners") == true,
            "replacement corners count at the temporary peak even when final topology would fit");
        Check(MeshPlans.CreationLimit(3,3,ushort.MaxValue,3,triangle,Array.Empty<(int,int)>(),replacement)?.Contains("faces") == true,
            "native face creation capacity is checked before old faces are removed");
        var onePoint = replacement with { Points = new() { new(new Vector3(.5f,0,0),new[] { (0,1f) }) } };
        Check(MeshPlans.CreationLimit(ushort.MaxValue,3,1,3,triangle,Array.Empty<(int,int)>(),onePoint)?.Contains("points") == true,
            "native vertex creation capacity is checked even for a tiny edit");
        Check(MeshPlans.CreationLimit(int.MaxValue,3,1,3,triangle,Array.Empty<(int,int)>(),onePoint)?.Contains("points") == true,
            "count arithmetic cannot wrap and admit an oversized mesh");
        Check(MeshPlans.CreationLimit(3,ushort.MaxValue,1,3,triangle,Array.Empty<(int,int)>(),replacement) == null,
            "reusing existing edges does not consume edge capacity");
        var cut = new MeshPlans.Rebuild(new() { 0 },new() { new(new[] { 0,3,2 },0),new(new[] { 3,1,2 },0) },
            new() { new(new Vector3(.5f,0,0),new[] { (0,.5f),(1,.5f) }) },null);
        Check(MeshPlans.CreationLimit(3,ushort.MaxValue-2,1,3,triangle,Array.Empty<(int,int)>(),cut)?.Contains("edges") == true,
            "only three unique new cut edges count, with their shared edge counted once");
        Check(MeshPlans.CreationLimit(3,ushort.MaxValue-3,1,3,triangle,Array.Empty<(int,int)>(),cut) == null,
            "duplicate references to a shared new edge do not overcount its creation");
        var joinLoose = cut with { Points = new() };
        Check(MeshPlans.CreationLimit(4,ushort.MaxValue-2,1,3,triangle,new[] { (3,2) },joinLoose) == null,
            "a reused loose edge is recognized regardless of endpoint order");
        Check(MeshPlans.CreationLimit(-1,3,1,3,triangle,Array.Empty<(int,int)>(),replacement) != null,
            "invalid negative counts fail safely");

        // Create Hole (review GEO-01): a 32-point ring and the game's fan on a quad add 32 points, 68 edges, 36 faces and
        // 108 corners at most; each count is checked against the limit before the game's native calls.
        int L = MeshPlans.NativeElementLimit;
        Check(MeshPlans.HoleLimit(L-32,L-68,L-36,L-108,4,32) == null, "a hole that exactly fills every count is allowed");
        Check(MeshPlans.HoleLimit(L-31,10,10,10,4,32)?.Contains("points") == true, "the ring's points are counted");
        Check(MeshPlans.HoleLimit(10,L-67,10,10,4,32)?.Contains("edges") == true, "the ring's and the fan's edges are counted");
        Check(MeshPlans.HoleLimit(10,10,L-35,10,4,32)?.Contains("faces") == true, "the fan's faces are counted");
        Check(MeshPlans.HoleLimit(10,10,10,L-107,4,32)?.Contains("face corners") == true, "the fan's face corners are counted");
        // Merge Faces (review GEO-01): the merged faces are made before the old ones go.
        var square = new[] { new[] { 0,1,2 },new[] { 0,2,3 } };
        var merged = new MeshPlans.Rebuild(new() { 0,1 },new() { new(new[] { 0,1,2,3 },0) },new(),null);
        Check(MeshPlans.CreationLimit(4,5,2,L-3,square,Array.Empty<(int,int)>(),merged)?.Contains("face corners") == true,
            "a merge whose four new corners pass the limit is refused before it starts");
        Check(MeshPlans.CreationLimit(4,5,2,L-4,square,Array.Empty<(int,int)>(),merged) == null, "a merge that just fits goes ahead");
        // OBJ import (review DATA-01): one structure, so the same limit.
        Check(MeshPlans.NativeLimit(L,L,L,L,"x") == null && MeshPlans.NativeLimit(L+1L,0,0,0,"this OBJ")?.Contains("65,536 points") == true,
            "the shared limit allows exactly 65,535 and names the count past it");
        Console.WriteLine($"NATIVE_MESH_BUDGET_TESTS_OK: {checks} checks passed");
    }
}
