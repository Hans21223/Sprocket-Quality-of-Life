using System.Numerics;
using SprocketQoL;

/// Regression fixtures for edge rounding safety. The report did not identify its starting
/// mesh; the thin shells below are deliberately synthetic, not a claimed reproduction.
static class SmoothEdgeRegressionTests
{
    static int checks;
    sealed record Shell(List<Vector3> Points, List<int[]> Faces);
    static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }
    static IEnumerable<(int A, int B)> Sides(int[] face) =>
        face.Select((v, i) => (v, face[(i + 1) % face.Length]));
    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
    static (int, int)[] Edges(Shell shell) => shell.Faces.SelectMany(Sides)
        .Select(e => Key(e.A, e.B)).Distinct().ToArray();
    static Vector3 Area(IReadOnlyList<Vector3> points, int[] face)
    {
        Vector3 n = Vector3.Zero, origin = points[face[0]];
        for (int i = 1; i + 1 < face.Length; i++)
            n += Vector3.Cross(points[face[i]] - origin, points[face[i + 1]] - origin);
        return n;
    }
    static float Extent(IReadOnlyList<Vector3> points) =>
        Vector3.Distance(points.Aggregate(Vector3.Min), points.Aggregate(Vector3.Max));
    static double Volume(IReadOnlyList<Vector3> points, IEnumerable<int[]> faces)
    {
        // Shift first: the invariant must not lose precision merely because the part moved.
        Vector3 origin = points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Count;
        double volume = 0;
        foreach (var face in faces)
            for (int i = 1; i + 1 < face.Length; i++)
                volume += Vector3.Dot(points[face[0]] - origin,
                    Vector3.Cross(points[face[i]] - origin, points[face[i + 1]] - origin)) / 6d;
        return volume;
    }
    static Shell Orient(List<Vector3> points, IEnumerable<int[]> faces)
    {
        Vector3 centre = points.Aggregate(Vector3.Zero, (a, b) => a + b) / points.Count;
        return new(points, faces.Select(face =>
        {
            var result = face.ToArray();
            Vector3 middle = result.Aggregate(Vector3.Zero, (a, v) => a + points[v]) / result.Length;
            if (Vector3.Dot(Area(points, result), middle - centre) < 0) Array.Reverse(result);
            return result;
        }).ToList());
    }
    static Shell Cube() => Orient(Enumerable.Range(0, 8)
        .Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)).ToList(),
        new[] { new[] { 0, 1, 5, 4 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 },
            new[] { 1, 3, 7, 5 }, new[] { 0, 1, 3, 2 }, new[] { 4, 5, 7, 6 } });
    static Shell Octahedron()
    {
        var points = new List<Vector3> { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY,
            -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        var faces = new List<int[]>();
        foreach (int a in new[] { 0, 1 }) foreach (int b in new[] { 2, 3 })
            foreach (int c in new[] { 4, 5 }) faces.Add(new[] { a, b, c });
        return Orient(points, faces);
    }
    static Shell DoubleDisc(float halfHeight, int count = 16)
    {
        var points = Enumerable.Range(0, count).Select(i =>
            new Vector3(MathF.Cos(2 * MathF.PI * i / count), 0, MathF.Sin(2 * MathF.PI * i / count))).ToList();
        points.Add(new Vector3(0, halfHeight, 0));
        points.Add(new Vector3(0, -halfHeight, 0));
        var faces = new List<int[]>();
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            // These remain consistently opposed even for the exactly flat rejection fixture.
            faces.Add(new[] { count, next, i });
            faces.Add(new[] { count + 1, i, next });
        }
        return new(points, faces);
    }
    static Shell ThinCylinder(float halfHeight, int count = 16)
    {
        var points = Enumerable.Range(0, count * 2).Select(i =>
            new Vector3(MathF.Cos(2 * MathF.PI * (i % count) / count), i < count ? halfHeight : -halfHeight,
                MathF.Sin(2 * MathF.PI * (i % count) / count))).ToList();
        points.Add(new Vector3(0, halfHeight, 0)); points.Add(new Vector3(0, -halfHeight, 0));
        var faces = new List<int[]>();
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            faces.Add(new[] { 2 * count, next, i });
            faces.Add(new[] { 2 * count + 1, i + count, next + count });
            faces.Add(new[] { i, next, next + count, i + count });
        }
        return Orient(points, faces);
    }
    static Shell Placed(Shell source, bool move)
    {
        if (!move) return source;
        var rotation = Quaternion.CreateFromYawPitchRoll(.71f, -.32f, .43f);
        return new(source.Points.Select(p => Vector3.Transform(p, rotation) + new Vector3(13, -7, 4)).ToList(),
            source.Faces.Select(f => f.ToArray()).ToList());
    }
    static MeshPlans.Rebuild Plan(Shell shell, IEnumerable<(int, int)> edges, float width, int segments, string context)
    {
        var before = shell.Points.ToArray(); var oldFaces = shell.Faces.Select(f => f.ToArray()).ToArray();
        MeshPlans.Rebuild plan;
        try { plan = MeshPlans.Bevel(shell.Points, shell.Faces, edges, width, segments); }
        catch (Exception ex) { throw new Exception(context + ": planner threw instead of returning a safe rejection", ex); }
        Check(before.SequenceEqual(shell.Points) && oldFaces.Zip(shell.Faces).All(p => p.First.SequenceEqual(p.Second)),
            context + ": planning must leave the input unchanged");
        if (plan.Why != null)
            Check(plan.Add.Count == 0 && plan.Remove.Count == 0 && plan.Points.Count == 0,
                context + ": rejection must not expose a partial edit");
        return plan;
    }
    static void Rejected(Shell shell, IEnumerable<(int, int)> edges, float width, int segments, string context)
    {
        var plan = Plan(shell, edges, width, segments, context);
        Check(plan.Why != null, context + ": unsafe input should be rejected before applying the mesh");
    }
    static void ClosedResult(Shell source, MeshPlans.Rebuild plan, string context)
    {
        Check(plan.Why == null, context + ": expected a usable round: " + plan.Why);
        Check(MeshPlans.Check(source.Points, source.Faces, plan) == null,
            context + ": accepted result must also pass the shared apply checks: " + MeshPlans.Check(source.Points, source.Faces, plan));
        var points = source.Points.Concat(plan.Points.Select(p => p.P)).ToArray();
        var faces = source.Faces.Where((_, i) => !plan.Remove.Contains(i)).Concat(plan.Add.Select(f => f.Corners)).ToArray();
        var used = faces.SelectMany(f => f).Distinct().ToArray();
        Check(used.All(v => float.IsFinite(points[v].X) && float.IsFinite(points[v].Y) && float.IsFinite(points[v].Z)),
            context + ": finite geometry");
        Check(faces.All(f => f.Length is 3 or 4 && f.Distinct().Count() == f.Length), context + ": valid native face sizes");
        var edges = faces.SelectMany(Sides).GroupBy(e => Key(e.A, e.B)).ToArray();
        foreach (var edge in edges)
            Check(edge.Count() == 2 && edge.First().A == edge.Last().B, context + ": closed, oppositely wound shared edges");
        Check(used.Length - edges.Length + faces.Length == 2, context + ": preserve the simple closed shell topology");
        float tolerance = Math.Max(1e-8f, Extent(source.Points) * 1e-7f);
        for (int i = 0; i < used.Length; i++) for (int j = i + 1; j < used.Length; j++)
            Check(Vector3.DistanceSquared(points[used[i]], points[used[j]]) > tolerance * tolerance,
                context + ": no geometrically duplicated vertices or coincident curved strips");
        double volume = Volume(points, faces), originalVolume = Volume(source.Points, source.Faces);
        Check(volume > 0 && volume <= originalVolume * 1.001,
            context + ": convex rounding must retain positive volume without adding overlapping volume outside the original shell");
    }
    // Winding alone misses an inset that has crossed its opposite side twice. For an all-edge
    // convex round, any surviving portion of an old flat plate must satisfy every inset plane.
    static void ConvexInsets(Shell shell, MeshPlans.Rebuild plan, float width, string context)
    {
        var points = shell.Points.Concat(plan.Points.Select(p => p.P)).ToArray();
        float tolerance = Extent(shell.Points) * 2e-5f;
        foreach (int source in plan.Remove)
        {
            var face = shell.Faces[source]; Vector3 origin = shell.Points[face[0]];
            Vector3 normal = Vector3.Normalize(Area(shell.Points, face));
            foreach (var flat in plan.Add.Where(f => f.Source == source &&
                         f.Corners.All(v => Math.Abs(Vector3.Dot(points[v] - origin, normal)) < tolerance)))
                foreach (int v in flat.Corners) foreach (var edge in Sides(face))
                {
                    Vector3 inward = Vector3.Normalize(Vector3.Cross(normal, shell.Points[edge.B] - shell.Points[edge.A]));
                    Check(Vector3.Dot(points[v] - shell.Points[edge.A], inward) >= width - tolerance,
                        context + ": an inset plate has crossed the opposing rounded strip");
                }
        }
    }
    static void RoundedCubeSurface(Shell cube, MeshPlans.Rebuild plan, float radius, bool moved)
    {
        var points = cube.Points.Concat(plan.Points.Select(p => p.P)).ToArray();
        var faces = cube.Faces.Where((_, i) => !plan.Remove.Contains(i)).Concat(plan.Add.Select(f => f.Corners));
        var inverse = Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(.71f, -.32f, .43f));
        foreach (int vertex in faces.SelectMany(f => f).Distinct())
        {
            Vector3 point = moved ? Vector3.Transform(points[vertex] - new Vector3(13, -7, 4), inverse) : points[vertex];
            // Independent rounded-box signed-distance construction. It simultaneously checks
            // flat tangent plates, cylindrical edges and the spherical three-edge junctions.
            Vector3 closestCore = Vector3.Clamp(point, new Vector3(radius), new Vector3(1 - radius));
            Check(Math.Abs(Vector3.Distance(point, closestCore) - radius) < 2e-5f,
                "all-edge cube rounding must stay on the radius-tangent rounded box, without dented corner fans or bowed cylinder ends");
        }
    }
    internal static void Run()
    {
        checks = 0;
        foreach (bool move in new[] { false, true })
        {
            var cube = Placed(Cube(), move); var all = Edges(cube);
            foreach (int segments in new[] { 2, 8, 16 })
            {
                var small = Plan(cube, all, .1f, segments, "cube small");
                ClosedResult(cube, small, "cube small"); ConvexInsets(cube, small, .1f, "cube small");
                RoundedCubeSurface(cube, small, .1f, move);
                var near = Plan(cube, all, .49f, segments, "cube near half width");
                if (near.Why == null) { ClosedResult(cube, near, "cube near half width"); ConvexInsets(cube, near, .49f, "cube near half width"); }
                foreach (float width in new[] { .5f, .51f, .6f })
                    Rejected(cube, all, width, segments, "cube crossing/zero flat plate " + width);
            }
            var octa = Placed(Octahedron(), move); var octaEdges = Edges(octa);
            foreach (float width in new[] { .01f, .1f, .3f })
            {
                var plan = Plan(octa, octaEdges, width, 8, "octahedron");
                ClosedResult(octa, plan, "octahedron " + width); ConvexInsets(octa, plan, width, "octahedron " + width);
            }
            // Each equilateral face's inradius is sqrt(1/6), about .408248 m.
            foreach (float width in new[] { .41f, .45f, .5f, .7f })
                Rejected(octa, octaEdges, width, 8, "octahedron beyond face inradius " + width);
            foreach (float height in new[] { .1f, .01f, .001f })
            {
                var disc = Placed(DoubleDisc(height), move);
                var rim = Edges(disc).Where(e => e.Item1 < 16 && e.Item2 < 16).ToArray();
                foreach (float width in new[] { .001f, .01f, .1f })
                {
                    var plan = Plan(disc, rim, width, 8, "thin synthetic double disc");
                    if (plan.Why == null) ClosedResult(disc, plan, "thin synthetic double disc");
                }
                var cylinder = Placed(ThinCylinder(height), move);
                var rims = Edges(cylinder).Where(e => e.Item1 < 32 && e.Item2 < 32 && e.Item1 / 16 == e.Item2 / 16).ToArray();
                foreach (float width in new[] { .001f, .01f, .1f })
                {
                    var plan = Plan(cylinder, rims, width, 8, "thin synthetic cylinder");
                    if (plan.Why == null) ClosedResult(cylinder, plan, "thin synthetic cylinder");
                }
            }
            var flat = Placed(DoubleDisc(0), move);
            var flatRim = Edges(flat).Where(e => e.Item1 < 16 && e.Item2 < 16).ToArray();
            Rejected(flat, flatRim, .1f, 8, "zero-volume coincident double disc");
        }
        var input = Cube(); var inputEdges = Edges(input);
        foreach (float invalid in new[] { 0, -.1f, float.NaN, float.PositiveInfinity })
            Rejected(input, inputEdges, invalid, 8, "invalid width");
        foreach (int invalid in new[] { 0, -1, 17 }) Rejected(input, inputEdges, .1f, invalid, "invalid segment count");
        Rejected(input, Array.Empty<(int, int)>(), .1f, 8, "empty selection");
        Rejected(input, new[] { (-1, 0) }, .1f, 8, "invalid selected vertex");
        Rejected(input, new[] { (0, 7) }, .1f, 8, "diagonal is not an edge");
        var invalidPoints = Cube(); invalidPoints.Points[0] = new(float.NaN, 0, 0);
        Rejected(invalidPoints, new[] { (0, 1) }, .1f, 8, "invalid source point");
        var invalidFaces = Cube(); invalidFaces.Faces[0][2] = 99;
        Rejected(invalidFaces, new[] { (0, 1) }, .1f, 8, "invalid unselected face corner");
        var repeated = Cube(); repeated.Faces[0][2] = repeated.Faces[0][1];
        Rejected(repeated, new[] { (0, 1) }, .1f, 8, "repeated source face corner");
        Console.WriteLine($"SMOOTH_EDGE_REGRESSION_TESTS_OK: {checks} checks");
    }
}
