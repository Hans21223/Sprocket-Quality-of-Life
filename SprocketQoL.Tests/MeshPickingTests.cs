using System.Numerics;
using SprocketQoL;
using static SprocketQoL.MeshPicking;

static class MeshPickingTests
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Near(float actual, float expected, float tolerance, string message) => Check(Math.Abs(actual - expected) <= tolerance, message);

    internal static void Run()
    {
        checks = 0;
        var pointer = new Vector2(100, 200);
        var points = new[] { new ScreenPoint(7, pointer + new Vector2(8, 0), 1), new ScreenPoint(2, pointer + new Vector2(2, 0), 20) };
        Check(PickPoint(points, pointer, 10)?.Id == 2, "point picking uses pointer distance before camera distance");
        Check(PickPoint(points, pointer, 1) == null, "vertices outside the pixel radius are not picked");
        Check(PickPoint(points, pointer, 2)?.Id == 2, "the pick radius includes its exact boundary");
        var pointHit = PickPoint(new[] { new ScreenPoint(4, pointer + new Vector2(3, 4), 5) }, pointer, 5)!.Value;
        Near(pointHit.DistancePixels, 5, 1e-6f, "point hit reports physical pixel distance");
        Check(pointHit.ClosestPixel == pointer + new Vector2(3, 4), "point hit retains its projected location");
        var ties = new[] { new ScreenPoint(9, pointer, 8), new ScreenPoint(5, pointer, 2), new ScreenPoint(3, pointer, 2) };
        foreach (var order in Permutations(ties)) Check(PickPoint(order, pointer, 2)?.Id == 3, "point ties resolve by frontmost depth then stable identifier");
        var chainedTies = new[] { new ScreenPoint(1, new Vector2(1, 0), 20), new ScreenPoint(2, new Vector2(1.0008f, 0), 10), new ScreenPoint(3, new Vector2(1.0016f, 0), 1) };
        foreach (var order in Permutations(chainedTies)) Check(PickPoint(order, Vector2.Zero, 2)?.Id == 2, "subpixel tie groups use the global closest distance and are order independent");
        Check(PickPoint(new[] { new ScreenPoint(1, new Vector2(1, 0), 20), new ScreenPoint(2, new Vector2(1.01f, 0), 1) }, Vector2.Zero, 2)?.Id == 1,
            "depth cannot override a clearly nearer screen-space candidate");
        var filtered = new[] { new ScreenPoint(1, pointer, -1), new ScreenPoint(2, pointer, 0), new ScreenPoint(3, pointer, 1, false), new ScreenPoint(4, pointer, 5) };
        Check(PickPoint(filtered, pointer, 2)?.Id == 4, "behind-camera and occluded points are ignored");
        Check(PickPoint(filtered, pointer, 2, includeHidden: true)?.Id == 3, "x-ray picking can include hidden points but still rejects points behind the camera");
        var pointVisits = new List<int>();
        var pointFallback = PickPoint(points, pointer, 10, accept: hit => { pointVisits.Add(hit.Id); return hit.Id == 7; });
        Check(pointFallback?.Id == 7 && pointVisits.SequenceEqual(new[] { 2, 7 }), "point visibility is checked nearest-first and can fall back to the next candidate");
        pointVisits.Clear();
        Check(PickPoint(points, pointer, 10, accept: hit => { pointVisits.Add(hit.Id); return true; })?.Id == 2 && pointVisits.Count == 1,
            "point visibility checks stop once the first eligible hit succeeds");
        Check(PickPoint(new[] { new ScreenPoint(1, new Vector2(float.NaN, 0), 1), new ScreenPoint(2, pointer, float.PositiveInfinity) }, pointer, 2) == null,
            "invalid point coordinates and depths are ignored");
        Check(PickPoint(null, pointer, 2) == null && PickPoint(Array.Empty<ScreenPoint>(), pointer, 2) == null, "empty point inputs are safe");
        foreach (float radius in new[] { -1f, float.NaN, float.PositiveInfinity }) Check(PickPoint(points, pointer, radius) == null, "invalid point radii are safe");
        Check(PickPoint(points, new Vector2(float.NaN, 0), 10) == null, "an invalid pointer is not picked");
        var hugePoint = PickPoint(new[] { new ScreenPoint(1, new Vector2(1e20f, 0), 1) }, Vector2.Zero, 2e20f)!.Value;
        Check(float.IsFinite(hugePoint.DistancePixels) && hugePoint.Id == 1, "finite large pixel coordinates do not overflow squared distances");

        var longEdge = new ScreenEdge(4, new Vector2(-1000, 0), new Vector2(1000, 0), 10, 10);
        var edgeHit = PickEdge(new[] { longEdge }, new Vector2(13, 3), 5)!.Value;
        Near(edgeHit.DistancePixels, 3, 1e-6f, "long edges are picked at their closest point rather than their midpoint");
        Near(edgeHit.ClosestPixel.X, 13, 1e-5f, "edge closest point lies beneath the pointer");
        Near(edgeHit.SegmentT, .5065f, 1e-6f, "edge hit retains its segment parameter");
        var endpoint = PickEdge(new[] { new ScreenEdge(1, Vector2.Zero, new Vector2(10, 0), 2, 4) }, new Vector2(12, 3), 4)!.Value;
        Near(endpoint.SegmentT, 1, 1e-6f, "edge projection clamps beyond the endpoint");
        Near(endpoint.DistancePixels, MathF.Sqrt(13), 1e-6f, "endpoint distance includes both screen axes");
        var perspective = new ScreenEdge(1, Vector2.Zero, new Vector2(100, 0), 1, 10);
        var perspectiveHit = PickEdge(new[] { perspective }, new Vector2(50, 0), 1)!.Value;
        Near(perspectiveHit.SegmentT, 1f / 11, 1e-6f, "perspective picking returns world-linear parameter rather than screen-linear parameter");
        Near(perspectiveHit.Depth, 20f / 11, 1e-6f, "perspective depth is interpolated reciprocally");
        var reversed = perspective with { A = perspective.B, B = perspective.A, DepthA = perspective.DepthB, DepthB = perspective.DepthA };
        Near(PickEdge(new[] { reversed }, new Vector2(50, 0), 1)!.Value.SegmentT, 1 - perspectiveHit.SegmentT, 1e-6f,
            "reversing an edge reverses its original parameter without changing the picked screen location");
        var orthographicHit = PickEdge(new[] { perspective }, new Vector2(50, 0), 1, orthographic: true)!.Value;
        Near(orthographicHit.SegmentT, .5f, 1e-6f, "orthographic picking uses linear interpolation");
        Near(orthographicHit.Depth, 5.5f, 1e-6f, "orthographic depth stays linear");
        var partial = perspective with { From = .2f, To = .8f };
        Near(PickEdge(new[] { partial }, new Vector2(50, 0), 1)!.Value.SegmentT, .2f + .6f / 11, 1e-6f,
            "a clipped edge maps its hit back to the original uncut segment");
        var collapsed = PickEdge(new[] { new ScreenEdge(6, pointer, pointer, 10, 2) }, pointer, 0)!.Value;
        Near(collapsed.SegmentT, 1, 1e-6f, "an edge projected to one pixel picks its frontmost endpoint");
        Near(collapsed.Depth, 2, 1e-6f, "a degenerate screen-space edge remains finite");
        var edgeTies = new[] { longEdge with { Id = 9, DepthA = 8, DepthB = 8 }, longEdge with { Id = 5, DepthA = 2, DepthB = 2 }, longEdge with { Id = 3, DepthA = 2, DepthB = 2 } };
        foreach (var order in Permutations(edgeTies)) Check(PickEdge(order, new Vector2(0, 1), 2)?.Id == 3, "edge ties resolve by frontmost depth then stable identifier");
        var edgeVisits = new List<int>();
        var edgeFallback = PickEdge(edgeTies, new Vector2(0, 1), 2, accept: hit => { edgeVisits.Add(hit.Id); return hit.Id == 5; });
        Check(edgeFallback?.Id == 5 && edgeVisits.SequenceEqual(new[] { 3, 5 }), "edge visibility checks follow stable tie order and stop at the first visible hit");
        Check(PickEdge(edgeTies, new Vector2(0, 1), 2, accept: _ => false) == null, "no hit is returned when every edge is occluded");
        edgeVisits.Clear();
        var differentDistances = new[] { longEdge with { Id = 1 }, longEdge with { Id = 2, A = longEdge.A + new Vector2(0, 3), B = longEdge.B + new Vector2(0, 3) } };
        Check(PickEdge(differentDistances, new Vector2(0, 1), 3, accept: hit => { edgeVisits.Add(hit.Id); return hit.Id == 2; })?.Id == 2
            && edgeVisits.SequenceEqual(new[] { 1, 2 }), "an occluded nearest edge falls back to a farther visible edge inside the pixel radius");
        var hiddenEdges = new[] { longEdge with { Id = 1, Visible = false }, longEdge with { Id = 2, DepthA = 20, DepthB = 20 } };
        Check(PickEdge(hiddenEdges, Vector2.Zero, 1)?.Id == 2 && PickEdge(hiddenEdges, Vector2.Zero, 1, includeHidden: true)?.Id == 1, "edge visibility filtering respects x-ray mode");
        foreach (var invalid in new[] { longEdge with { A = new Vector2(float.NaN, 0) }, longEdge with { DepthA = 0 }, longEdge with { DepthB = -1 }, longEdge with { DepthA = float.PositiveInfinity }, longEdge with { From = -.1f }, longEdge with { To = 1.1f }, longEdge with { From = .7f, To = .2f }, longEdge with { To = float.NaN } })
            Check(PickEdge(new[] { invalid }, Vector2.Zero, 2) == null, "invalid projected edges are ignored");
        Check(PickEdge(null, Vector2.Zero, 2) == null && PickEdge(Array.Empty<ScreenEdge>(), Vector2.Zero, 2) == null, "empty edge inputs are safe");
        foreach (float radius in new[] { -1f, float.NaN, float.PositiveInfinity }) Check(PickEdge(new[] { longEdge }, Vector2.Zero, radius) == null, "invalid edge radii are safe");
        Check(PickEdge(new[] { longEdge }, new Vector2(float.PositiveInfinity, 0), 3) == null, "invalid edge pointers are ignored");
        var hugeEdge = new ScreenEdge(7, new Vector2(-1e30f, 0), new Vector2(1e30f, 0), 1, 1);
        var hugeEdgeHit = PickEdge(new[] { hugeEdge }, Vector2.Zero, 0)!.Value;
        Near(hugeEdgeHit.SegmentT, .5f, 1e-6f, "long finite edge coordinates do not overflow closest-point calculations");
        var translation = new Vector2(1200, -700);
        const float scale = 3.5f;
        var transformed = perspective with { A = perspective.A * scale + translation, B = perspective.B * scale + translation };
        var transformedHit = PickEdge(new[] { transformed }, new Vector2(50, 2) * scale + translation, 3 * scale)!.Value;
        Near(transformedHit.SegmentT, perspectiveHit.SegmentT, 1e-6f, "pixel translation and uniform scale preserve the picked edge parameter");
        Near(transformedHit.DistancePixels, 2 * scale, 1e-5f, "pixel radius and distances scale together");

        Check(TryClipNear(2, 10, 1, out var front) && front == new SegmentClip(0, 1), "a visible edge is unchanged by near clipping");
        Check(!TryClipNear(-2, .5f, 1, out _), "an edge entirely behind the near plane is rejected");
        Check(TryClipNear(-2, 10, 1, out var entering), "a crossing edge survives near clipping");
        Near(entering.From, .25f, 1e-6f, "near clip computes the original segment entry parameter");
        Near(entering.To, 1, 1e-6f, "near clip keeps the visible endpoint");
        Check(TryClipNear(10, -2, 1, out var leaving), "reversed crossing edge survives near clipping");
        Near(leaving.To, .75f, 1e-6f, "reversed near clip computes the original segment exit parameter");
        Check(TryClipNear(1, 1, 1, out var boundary) && boundary == new SegmentClip(0, 1), "an edge on the near plane is accepted without division by zero");
        Check(TryClipNear(1, -1, 1, out var touching) && touching == new SegmentClip(0, 0), "an edge touching the near plane can become a point");
        foreach (var invalid in new[] { (float.NaN, 2f, 1f), (2f, float.PositiveInfinity, 1f), (2f, 3f, 0f), (2f, 3f, -1f), (2f, 3f, float.NaN) })
            Check(!TryClipNear(invalid.Item1, invalid.Item2, invalid.Item3, out _), "invalid near clipping inputs fail safely");

        // Derive the pointer from a known world-space location through a pinhole camera,
        // clip its original edge, and verify round-trip parameter recovery at many depths.
        foreach (float zA in new[] { -2f, .2f, 1f, 10f }) foreach (float zB in new[] { 1f, 4f, 20f })
        {
            var a = new Vector3(-1, .5f, zA); var b = new Vector3(3, -.2f, zB);
            if (!TryClipNear(zA, zB, .5f, out var clip)) continue;
            Vector3 ca = Vector3.Lerp(a, b, clip.From), cb = Vector3.Lerp(a, b, clip.To);
            Vector2 Project(Vector3 p) => new(300 * p.X / p.Z + 500, 300 * p.Y / p.Z + 400);
            var edge = new ScreenEdge(1, Project(ca), Project(cb), ca.Z, cb.Z, From: clip.From, To: clip.To);
            foreach (float local in new[] { 0f, .1f, .5f, .9f, 1f })
            {
                float originalT = clip.From + (clip.To - clip.From) * local;
                var world = Vector3.Lerp(a, b, originalT);
                var hit = PickEdge(new[] { edge }, Project(world), .01f)!.Value;
                Near(hit.SegmentT, originalT, 2e-5f, "perspective near-clipped hit recovers the original world-space parameter");
                Near(hit.Depth, world.Z, 3e-4f, "perspective near-clipped hit recovers camera-forward depth");
            }
        }
        // Visibility skips only the target's own faces: a fake ray with faces at fixed distances along it.
        (int Face, float At)? Cast((int Face, float At)[] faces, float from, float length)
        {
            (int Face, float At)? nearest = null;
            foreach (var f in faces)
                if (f.At >= from && f.At - from <= length && (nearest == null || f.At - from < nearest.Value.At)) nearest = (f.Face, f.At - from);
            return nearest;
        }
        bool Seen(float distance, int[] own, params (int Face, float At)[] faces) => InView(distance, .001f, own, (from, length) => Cast(faces, from, length));
        Check(Seen(10, new[] { 1 }), "nothing in the way: visible");
        Check(Seen(10, new[] { 1, 2 }, (1, 9.9f), (2, 9.95f)), "the target's own faces, rounded in front of it, don't hide it");
        Check(!Seen(10, new[] { 1 }, (1, 9.9f), (5, 9.95f)), "another face behind an own one still hides the target");
        Check(!Seen(10, new[] { 1 }, (5, 4f)), "another face in front hides the target");
        Check(Seen(10, Array.Empty<int>(), (5, 10f)), "a face at the target itself doesn't hide it");
        Check(!InView(10, .001f, new[] { 1 }, (from, length) => (1, 0f)), "an own face hit over and over stops, counted as hiding");

        // Select linked: points joined through faces, not across a gap; a loose point stays itself.
        var islands = new List<int[]> { new[] { 0, 1, 2, 3 }, new[] { 2, 3, 4, 5 }, new[] { 6, 7, 8 } };
        Check(Linked(islands, new[] { 0 }).SetEquals(new[] { 0, 1, 2, 3, 4, 5 }), "select linked follows shared corners across faces");
        Check(Linked(islands, new[] { 8 }).SetEquals(new[] { 6, 7, 8 }) && Linked(islands, new[] { 9 }).SetEquals(new[] { 9 }), "select linked stops at a separate piece; a loose point is just itself");
        Check(Linked(islands, Array.Empty<int>()).Count == 0, "nothing selected: nothing linked");
        Console.WriteLine($"MESH_PICKING_TESTS_OK: {checks} checks passed");
    }

    static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length == 0) { yield return Array.Empty<T>(); yield break; }
        for (int i = 0; i < items.Length; i++)
            foreach (var tail in Permutations(items.Where((_, j) => j != i).ToArray())) yield return new[] { items[i] }.Concat(tail).ToArray();
    }
}
