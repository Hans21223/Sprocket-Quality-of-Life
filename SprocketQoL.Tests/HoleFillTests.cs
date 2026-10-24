using System.Numerics;
using SprocketQoL;

static class HoleFillTests
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception(why); }
    static (List<Vector3> P, List<int> Outer, List<int> Inner, List<int[]> Faces) Plan(Fill.Mode mode, int segments = 32)
    {
        var p = new List<Vector3> { new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0) };
        var outer = Enumerable.Range(0, 4).ToList();
        var inner = Enumerable.Range(4, segments).ToList();
        p.AddRange(Enumerable.Range(0, segments).Select(k =>
        {
            double a = 2 * Math.PI * (k + .5) / segments;
            return new Vector3((float)Math.Cos(a) * .25f, (float)Math.Sin(a) * .25f, 0);
        }));
        var faces = Fill.Region(p, outer, new() { inner }, Vector3.UnitZ, mode == Fill.Mode.Fewest ? null : new(), mode == Fill.Mode.Light, mode);
        return (p, outer, inner, faces);
    }
    internal static void Run()
    {
        checks = 0;
        foreach (Fill.Mode mode in Enum.GetValues<Fill.Mode>().Distinct())
        {
            var plan = Plan(mode);
            var before = plan.P.ToArray();
            string? reason = HoleFill.Check(plan.P, plan.Outer, plan.Inner, plan.Faces, Vector3.UnitZ);
            Check(reason == null, $"valid 32-segment {mode} fill: {reason}");
            Check(plan.P.SequenceEqual(before), "validation does not move planned points");
            var rotate = Matrix4x4.CreateFromYawPitchRoll(.71f, .37f, 1.13f);
            var normal = Vector3.TransformNormal(Vector3.UnitZ, rotate);
            var moved = plan.P.Select(p => Vector3.Transform(p, rotate) + new Vector3(23, -51, 17)).ToArray();
            Check(HoleFill.Check(moved, plan.Outer, plan.Inner, plan.Faces, normal) == null, "rotated and translated fill remains valid");
            Check(HoleFill.Check(plan.P, plan.Outer.AsEnumerable().Reverse().ToArray(), plan.Inner,
                plan.Faces.Select(f => f.Reverse().ToArray()).ToArray(), -Vector3.UnitZ) == null, "reversed outward winding remains valid");
            var mirror = plan.P.Select(p => new Vector3(-p.X, p.Y, p.Z)).ToArray();
            Check(HoleFill.Check(mirror, plan.Outer, plan.Inner, plan.Faces, -Vector3.UnitZ) == null, "mirrored plate remains valid");
        }
        var good = Plan(Fill.Mode.Fewest);
        Check(HoleFill.Check(good.P, good.Outer, good.Inner, good.Faces.Skip(1).ToArray(), Vector3.UnitZ) != null, "a dropped face is rejected");
        Check(HoleFill.Check(good.P, good.Outer, good.Inner, good.Faces.Concat(new[] { good.Faces[0] }).ToArray(), Vector3.UnitZ) != null, "overlapping duplicate face is rejected");
        var backwards = good.Faces.Select(f => f.ToArray()).ToList(); backwards[0] = backwards[0].Reverse().ToArray();
        Check(HoleFill.Check(good.P, good.Outer, good.Inner, backwards, Vector3.UnitZ) != null, "one reversed surrounding face is rejected");
        var absent = good.P.Select((p, i) => i < 4 ? p : Vector3.Zero).ToArray();
        Check(HoleFill.Check(absent, good.Outer, good.Inner, new[] { good.Outer.ToArray() }, Vector3.UnitZ) != null, "a solid plate replacing a collapsed 32-point hole is rejected");
        var collapsedFill = Fill.Region(absent.ToList(), good.Outer, new() { good.Inner }, Vector3.UnitZ, null);
        Check(collapsedFill.Count > 0 && collapsedFill.All(f => f.Length is 3 or 4 && f.Distinct().Count() == f.Length),
            "the old per-face checks accept triangulation of a collapsed ring");
        Check(HoleFill.Check(absent, good.Outer, good.Inner, collapsedFill, Vector3.UnitZ) != null,
            "the complete hole check rejects a collapsed ring even when its generated faces look valid individually");
        var seam = good.P.Select((p, i) => i < 4 ? p : new Vector3(1, 0, 0)).ToArray();
        Check(HoleFill.Check(seam, good.Outer, good.Inner, good.Faces, Vector3.UnitZ) != null, "a collapsed ring on a seam is rejected");
        var noHole = Fill.Region(good.P.ToList(), good.Outer, new(), Vector3.UnitZ, null);
        Check(HoleFill.Check(good.P, good.Outer, good.Inner, noHole, Vector3.UnitZ) != null, "a fill omitting a real hole's boundary is rejected");
        var touched = good.P.Select((p, i) => i < 4 ? p : p + new Vector3(.8f, 0, 0)).ToArray();
        Check(HoleFill.Check(touched, good.Outer, good.Inner, good.Faces, Vector3.UnitZ) != null, "a hole crossing the plate rim is rejected");
        var crossed = good.Inner.ToArray(); (crossed[0], crossed[16]) = (crossed[16], crossed[0]);
        Check(HoleFill.Check(good.P, good.Outer, crossed, good.Faces, Vector3.UnitZ) != null, "self-crossing hole rim is rejected");
        var nan = good.P.ToArray(); nan[4] = new(float.NaN, 0, 0);
        Check(HoleFill.Check(nan, good.Outer, good.Inner, good.Faces, Vector3.UnitZ) != null, "nonfinite hole points are rejected");
        var missing = good.Faces.Select(f => f.ToArray()).ToArray(); missing[0][0] = good.P.Count;
        Check(HoleFill.Check(good.P, good.Outer, good.Inner, missing, Vector3.UnitZ) != null, "missing face point is rejected");
        Check(HoleFill.Check(good.P, good.Outer, good.Inner, new[] { new[] { 0, 1, 2, 3, 4 } }, Vector3.UnitZ) != null, "unsupported five-corner face is rejected");
        Check(HoleFill.Check(good.P, good.Outer, good.Inner,
            good.Faces.Skip(1).Concat(new[] { good.Faces[1] }).ToArray(), Vector3.UnitZ) != null,
            "balanced dropped and overlapping faces cannot bypass boundary coverage");
        MergedHoles();
        CombinedSweep();
        Console.WriteLine($"Hole fill: {checks} checks passed.");
    }

    /// Create Hole on touching faces: one hole through the area they make, centred on the whole area (not on one face),
    /// keeping the points along its sides so neighbouring plates stay joined, and passing the mesh checks.
    static void MergedHoles()
    {
        // A 2 x 1 plate of two quads facing +z, and a third quad bent 90° down at its right side.
        var pos = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0), new(0, 1, 0), new(2, 0, -1), new(2, 1, -1) };
        var faces = new List<int[]> { new[] { 0, 1, 4, 5 }, new[] { 1, 2, 3, 4 }, new[] { 2, 6, 7, 3 } };
        var areas = HoleMerge.Areas(pos, faces, new[] { 0, 1, 2 });
        Check(areas.Count == 2 && areas.Any(a => a.OrderBy(f => f).SequenceEqual(new[] { 0, 1 })) && areas.Any(a => a.SequenceEqual(new[] { 2 })),
              "merged hole: flat touching faces make one area, a face bent 90° stays apart");
        foreach (Fill.Mode mode in Enum.GetValues<Fill.Mode>().Distinct())
        {
            var plan = HoleMerge.Plan(pos, faces, new[] { new List<int> { 0, 1 } }, 16, 1, true, mode);
            Check(plan.Why == null && MeshPlans.Check(pos, faces, plan, gaps: false) is null, $"merged hole ({mode}): a valid plan: {plan.Why}");
            Check(plan.Remove.OrderBy(f => f).SequenceEqual(new[] { 0, 1 }), $"merged hole ({mode}): both faces go");
            var ring = plan.Points.Take(16).Select(p => p.P).ToArray();
            var middle = ring.Aggregate(Vector3.Zero, (s, p) => s + p) / ring.Length;
            Check(Vector3.Distance(middle, new Vector3(1, .5f, 0)) < 1e-4f && ring.All(p => Math.Abs(Vector3.Distance(p, middle) - .45f) < 1e-4f),
                  $"merged hole ({mode}): the ring is round the middle of both faces, 90% of the room to the sides");
            var at = pos.Concat(plan.Points.Select(p => p.P)).ToList();
            Check(plan.Add.All(f => Vector3.Dot(HoleRing.Normal(f.Corners.Select(v => at[v]).ToArray()), Vector3.UnitZ) > 0),
                  $"merged hole ({mode}): the faces round it face the way the plate did");
            Check(new[] { 1, 4 }.All(v => plan.Add.Any(f => f.Corners.Contains(v))), $"merged hole ({mode}): the points along the sides stay");
        }
        // An L of three squares: the middle by area is inside, so the ring fits there.
        var lPos = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0), new(0, 1, 0), new(1, 2, 0), new(0, 2, 0) };
        var lFaces = new List<int[]> { new[] { 0, 1, 4, 5 }, new[] { 1, 2, 3, 4 }, new[] { 5, 4, 6, 7 } };
        var lPlan = HoleMerge.Plan(lPos, lFaces, HoleMerge.Areas(lPos, lFaces, new[] { 0, 1, 2 }), 16, 1, true, Fill.Mode.Fewest);
        Check(lPlan.Why == null && MeshPlans.Check(lPos, lFaces, lPlan, gaps: false) is null, $"merged hole: an L-shaped area gets one hole: {lPlan.Why}");
        // Eight squares round a gap already: refused, not a second hole over the first.
        var gPos = Enumerable.Range(0, 16).Select(i => new Vector3(i % 4, i / 4, 0)).ToList();
        var gFaces = Enumerable.Range(0, 9).Where(c => c != 4).Select(c => { int i = c % 3, j = c / 3; return new[] { j * 4 + i, j * 4 + i + 1, (j + 1) * 4 + i + 1, (j + 1) * 4 + i }; }).ToList();
        var gAreas = HoleMerge.Areas(gPos, gFaces, Enumerable.Range(0, 8));
        Check(gAreas.Count == 1 && HoleMerge.Plan(gPos, gFaces, gAreas, 16, 1, true, Fill.Mode.Fewest).Why != null, "merged hole: an area round an existing hole is refused");
        // Two squares meeting only at a corner ((1, 0) and (0, 1) of the grid) are two areas.
        Check(HoleMerge.Areas(gPos, gFaces, new[] { 1, 3 }).Count == 2, "merged hole: faces touching only at a corner stay apart");
        // One lopsided face: centred on its corners' average, as the game cuts it (so the hover outline matches the cut),
        // not on its middle by area, (1.083, 0.417) here.
        var qPos = new List<Vector3> { new(0, 0, 0), new(3, 0, 0), new(1, 1, 0), new(0, 1, 0) };
        var one = HoleMerge.Ring(qPos, new List<int[]> { new[] { 0, 1, 2, 3 } }, new List<int> { 0 }, 16, 1, true, out var oneWhy);
        var oneMiddle = one is { } r ? r.Ring.Aggregate(Vector3.Zero, (s, p) => s + p) / r.Ring.Length : new Vector3(float.NaN);
        Check(Vector3.Distance(oneMiddle, new Vector3(1, .5f, 0)) < 1e-4f, $"single face: the hole is centred on the corners' average, as the game's: {oneMiddle} {oneWhy}");
    }

    static void CombinedSweep()
    {
        var shapes = new (string Name, Vector3[] Corners)[]
        {
            ("triangle", new[] { new Vector3(-1, -.8f, 0), new Vector3(1, -.8f, 0), new Vector3(0, 1.2f, 0) }),
            ("quad", new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(-1, 1, 0) }),
            ("skew quad", new[] { new Vector3(-1.6f, -.8f, 0), new Vector3(1.2f, -.6f, 0), new Vector3(.9f, 1.1f, 0), new Vector3(-.7f, .6f, 0) }),
            ("concave quad", new[] { new Vector3(-1, -1, 0), new Vector3(1, -1, 0), new Vector3(.1f, .1f, 0), new Vector3(-1, 1, 0) }),
        };
        var rotation = Matrix4x4.CreateFromYawPitchRoll(.71f, .37f, 1.13f) * Matrix4x4.CreateTranslation(7, -11, 3);
        var transforms = new (string Name, Matrix4x4 Matrix)[]
        {
            ("original", Matrix4x4.Identity),
            ("mirror", Matrix4x4.CreateScale(-1, 1, 1)),
            ("rotated mirror", Matrix4x4.CreateScale(-1, 1, 1) * rotation),
        };
        int combinations = 0;
        foreach (var shape in shapes)
        foreach (int segments in new[] { 4, 8, 16, 32, 64, 96 })
        foreach (float size in new[] { .1f, 1f, 3f })
        foreach (var transform in transforms)
        foreach (bool cardinalPoints in new[] { true, false })
        {
            // The concave fixture starts outside its plate, exercising the safe-centre search rather than
            // merely covering convex plates. The source radius stays physical when relative size changes.
            var centre = shape.Name == "concave quad" ? new Vector3(.7f, .7f, 0)
                : shape.Corners.Aggregate(Vector3.Zero, (sum, p) => sum + p) / shape.Corners.Length;
            var seed = Enumerable.Range(0, segments).Select(k => centre + new Vector3(
                MathF.Cos(k * MathF.Tau / segments), MathF.Sin(k * MathF.Tau / segments), 0) * .3f)
                .Select(p => Vector3.Transform(p, transform.Matrix)).ToArray();
            centre = Vector3.Transform(centre, transform.Matrix);
            var outer = shape.Corners.Select(p => Vector3.Transform(p, transform.Matrix)).ToArray();
            var before = outer.ToArray(); var seedBefore = seed.ToArray();
            string label = $"{shape.Name}, {segments} segments, {size * 100:0}% size, {transform.Name}, {(cardinalPoints ? "cardinal points" : "flat sides")}";
            Check(HoleRing.TryFit(outer, seed, centre, out var ring, out string fitNote, size, cardinalPoints), $"combined fit {label}: {fitNote}");
            Check(outer.SequenceEqual(before) && seed.SequenceEqual(seedBefore), "fitting never changes the supplied plate or native ring");
            Check(ring.Length == segments, "fitting keeps every requested hole vertex");
            var normal = Vector3.Normalize(HoleRing.Normal(outer));
            // Independently measure a regular polygon from its first chord, rather than relying on the
            // helper's area formula or its ring-centre estimate. This catches folded or uneven fitted rings.
            double chord = Vector3.Distance(ring[0], ring[1]);
            double radius = chord / (2 * Math.Sin(Math.PI / segments));
            double circleArea = segments * radius * radius * Math.Sin(2 * Math.PI / segments) * .5;
            double holeArea = SignedArea(ring, normal), plateArea = SignedArea(outer, normal);
            Check(holeArea > 0 && Math.Abs(holeArea - circleArea) <= Math.Max(1e-9, circleArea * .001), "fitted ring preserves a regular circular aperture");
            Check(Enumerable.Range(0, segments).All(k => Math.Abs(Vector3.Distance(ring[k], ring[(k + 1) % segments]) - chord)
                <= Math.Max(2e-6, chord * .001)), "all fitted ring segments have the same physical length");
            foreach (Fill.Mode mode in Enum.GetValues<Fill.Mode>().Distinct())
            {
                var p = outer.Concat(ring).ToList();
                var outerIds = Enumerable.Range(0, outer.Length).ToList();
                var innerIds = Enumerable.Range(outer.Length, segments).ToList();
                var faces = Fill.Region(p, outerIds, new() { innerIds }, normal,
                    mode == Fill.Mode.Fewest ? null : new(), mode == Fill.Mode.Light, mode);
                string? failure = HoleFill.Check(p, outerIds, innerIds, faces, normal);
                Check(failure == null, $"combined {label}, {mode}: {failure}");
                double filledArea = faces.Sum(f => SignedArea(f.Select(i => p[i]).ToArray(), normal));
                double wanted = plateArea - holeArea;
                Check(wanted > 0 && Math.Abs(filledArea - wanted) <= wanted * 1e-4,
                    $"combined {label}, {mode}: fill area equals original plate minus the fitted aperture");
                Check(faces.All(f => SignedArea(f.Select(i => p[i]).ToArray(), normal) > wanted * 1e-9),
                    "the combined pipeline emits no collapsed or inward faces");
                var uses = faces.SelectMany(f => f.Select((v, k) => (A: v, B: f[(k + 1) % f.Length])))
                    .GroupBy(e => e.A < e.B ? (e.A, e.B) : (e.B, e.A)).ToDictionary(g => g.Key, g => g.ToArray());
                var rim = new[] { outerIds, innerIds }.SelectMany(loop => loop.Select((v, k) =>
                {
                    int next = loop[(k + 1) % loop.Count];
                    return v < next ? (v, next) : (next, v);
                })).ToHashSet();
                Check(rim.All(e => uses.TryGetValue(e, out var list) && list.Length == 1),
                    "every source plate and fitted aperture segment survives exactly once");
                Check(uses.Where(e => !rim.Contains(e.Key)).All(e => e.Value.Length == 2 &&
                    e.Value[0].A == e.Value[1].B && e.Value[0].B == e.Value[1].A), "new interior seams have opposite, paired faces");
                Check(p.Take(outer.Length).SequenceEqual(before), "filling never moves the source plate corners");
                combinations++;
            }
        }
        Console.WriteLine($"Hole pipeline sweep: {combinations} fitted fills passed.");
    }

    static double SignedArea(IReadOnlyList<Vector3> loop, Vector3 normal)
    {
        // A fan about the first point avoids absolute-coordinate area cancellation after translation.
        double area = 0;
        var origin = loop[0];
        for (int k = 1; k + 1 < loop.Count; k++)
        {
            double ax = (double)loop[k].X - origin.X, ay = (double)loop[k].Y - origin.Y, az = (double)loop[k].Z - origin.Z;
            double bx = (double)loop[k + 1].X - origin.X, by = (double)loop[k + 1].Y - origin.Y, bz = (double)loop[k + 1].Z - origin.Z;
            area += (ay * bz - az * by) * normal.X + (az * bx - ax * bz) * normal.Y + (ax * by - ay * bx) * normal.Z;
        }
        return area * .5;
    }
}
