using System.Numerics;

namespace SprocketQoL;

/// Pointer picking in pixels. Projection and occlusion belong to the caller; depth is
/// positive camera-forward distance, as returned by Camera.WorldToScreenPoint.
public static class MeshPicking
{
    public readonly record struct ScreenPoint(int Id, Vector2 Pixel, float Depth, bool Visible = true);
    public readonly record struct ScreenEdge(int Id, Vector2 A, Vector2 B, float DepthA, float DepthB,
        bool Visible = true, float From = 0, float To = 1);
    public readonly record struct Hit(int Id, float DistancePixels, float Depth, float SegmentT, Vector2 ClosestPixel);
    public readonly record struct SegmentClip(float From, float To);

    // Resolve subpixel projection noise by depth, without allowing a visibly farther
    // point to win. Candidates are compared with the global minimum, so ties do not
    // depend on enumeration order or a non-transitive sequence of pairwise comparisons.
    public const float DistanceTiePixels = .001f;

    /// The optional acceptance callback can perform a visibility test. It runs in pick
    /// priority order and stops at the first accepted hit, rather than testing every point.
    public static Hit? PickPoint(IEnumerable<ScreenPoint>? candidates, Vector2 pointer, float radiusPixels,
        bool includeHidden = false, Func<Hit, bool>? accept = null)
        => PointCandidates(candidates, pointer, radiusPixels, includeHidden)?.Best(accept);

    /// The same ranking as a normal click, for deliberately cycling overlapping targets.
    /// A bounded list prevents dense meshes from doing unlimited visibility raycasts.
    public static IReadOnlyList<Hit> PointHits(IEnumerable<ScreenPoint>? candidates, Vector2 pointer, float radiusPixels,
        bool includeHidden = false, Func<Hit, bool>? accept = null, int limit = 32)
        => PointCandidates(candidates, pointer, radiusPixels, includeHidden)?.Ranked(accept, limit).ToArray() ?? Array.Empty<Hit>();

    static Nearest? PointCandidates(IEnumerable<ScreenPoint>? candidates, Vector2 pointer, float radiusPixels, bool includeHidden)
    {
        if (candidates == null || !Finite(pointer) || !float.IsFinite(radiusPixels) || radiusPixels < 0) return null;
        var nearest = new Nearest();
        double radius2 = (double)radiusPixels * radiusPixels;
        foreach (var point in candidates)
        {
            if (!point.Visible && !includeHidden || !Finite(point.Pixel) || !Positive(point.Depth)) continue;
            double dx = (double)point.Pixel.X - pointer.X, dy = (double)point.Pixel.Y - pointer.Y;
            double distance2 = dx * dx + dy * dy;
            if (distance2 <= radius2)
                nearest.Add(new Hit(point.Id, (float)Math.Sqrt(distance2), point.Depth, 0, point.Pixel));
        }
        return nearest;
    }

    /// The optional acceptance callback runs only until the first visible/eligible hit.
    /// SegmentT refers to the original edge, including any near-plane clipping range.
    public static Hit? PickEdge(IEnumerable<ScreenEdge>? candidates, Vector2 pointer, float radiusPixels,
        bool orthographic = false, bool includeHidden = false, Func<Hit, bool>? accept = null)
        => EdgeCandidates(candidates, pointer, radiusPixels, orthographic, includeHidden)?.Best(accept);

    public static IReadOnlyList<Hit> EdgeHits(IEnumerable<ScreenEdge>? candidates, Vector2 pointer, float radiusPixels,
        bool orthographic = false, bool includeHidden = false, Func<Hit, bool>? accept = null, int limit = 32)
        => EdgeCandidates(candidates, pointer, radiusPixels, orthographic, includeHidden)?.Ranked(accept, limit).ToArray() ?? Array.Empty<Hit>();

    static Nearest? EdgeCandidates(IEnumerable<ScreenEdge>? candidates, Vector2 pointer, float radiusPixels,
        bool orthographic, bool includeHidden)
    {
        if (candidates == null || !Finite(pointer) || !float.IsFinite(radiusPixels) || radiusPixels < 0) return null;
        var nearest = new Nearest();
        double radius2 = (double)radiusPixels * radiusPixels;
        foreach (var edge in candidates)
        {
            if (!edge.Visible && !includeHidden || !Finite(edge.A) || !Finite(edge.B)
                || !Positive(edge.DepthA) || !Positive(edge.DepthB)
                || !float.IsFinite(edge.From) || !float.IsFinite(edge.To) || edge.From < 0 || edge.To > 1 || edge.From > edge.To) continue;
            double dx = (double)edge.B.X - edge.A.X, dy = (double)edge.B.Y - edge.A.Y;
            double length2 = dx * dx + dy * dy;
            double screenT = length2 > 0
                ? Math.Clamp((((double)pointer.X - edge.A.X) * dx + ((double)pointer.Y - edge.A.Y) * dy) / length2, 0, 1)
                : edge.DepthA <= edge.DepthB ? 0 : 1;
            double x = edge.A.X + screenT * dx, y = edge.A.Y + screenT * dy;
            double px = pointer.X - x, py = pointer.Y - y, distance2 = px * px + py * py;
            if (distance2 > radius2) continue;
            // A screen-space midpoint is not a world-space midpoint under perspective.
            // Correct the interpolation before returning the original edge parameter.
            double edgeT = orthographic ? screenT
                : screenT * edge.DepthA / ((1 - screenT) * edge.DepthB + screenT * edge.DepthA);
            double depth = (1 - edgeT) * edge.DepthA + edgeT * edge.DepthB;
            float originalT = (float)(edge.From + edgeT * ((double)edge.To - edge.From));
            nearest.Add(new Hit(edge.Id, (float)Math.Sqrt(distance2), (float)depth, originalT, new Vector2((float)x, (float)y)));
        }
        return nearest;
    }

    /// Clip an original line segment before projecting it. Interpolate world endpoints
    /// with From/To, project those positions, and pass the same range to ScreenEdge.
    public static bool TryClipNear(float depthA, float depthB, float nearDepth, out SegmentClip clip)
    {
        clip = default;
        if (!float.IsFinite(depthA) || !float.IsFinite(depthB) || !Positive(nearDepth)) return false;
        if (depthA < nearDepth && depthB < nearDepth) return false;
        double from = 0, to = 1;
        if (depthA < nearDepth) from = ((double)nearDepth - depthA) / ((double)depthB - depthA);
        else if (depthB < nearDepth) to = ((double)nearDepth - depthA) / ((double)depthB - depthA);
        clip = new SegmentClip((float)from, (float)to);
        return true;
    }

    /// True when no face but the target's own lies on the ray before the target, `distance` along it (less `tolerance`).
    /// `cast(from, length)` gives the nearest face hit up to `length` beyond `from`: its index and distance from `from`.
    /// A face through the target can't hide it, yet at a crease seen from outside (a point under an overhang) its own
    /// faces come towards the camera, and rounding put them in front of it: those points couldn't be picked.
    public static bool InView(float distance, float tolerance, ICollection<int> own, Func<float, float, (int Face, float At)?> cast)
    {
        float from = 0;
        for (int skips = 0; skips <= 2 * own.Count; skips++) // each own face once, a bent one twice
        {
            float length = distance - tolerance - from;
            if (length <= 0 || cast(from, length) is not { } hit) return true;
            if (!own.Contains(hit.Face)) return false;
            from += hit.At + tolerance;
        }
        return false;
    }

    /// The points joined to the seeds through faces (Blender's select linked).
    public static HashSet<int> Linked(IReadOnlyList<int[]> faces, IEnumerable<int> seeds)
    {
        var byPoint = new Dictionary<int, List<int>>();
        for (int f = 0; f < faces.Count; f++)
            foreach (int v in faces[f]) { if (!byPoint.TryGetValue(v, out var list)) byPoint[v] = list = new(); list.Add(f); }
        var found = new HashSet<int>();
        var reached = new HashSet<int>();
        var todo = new Stack<int>(seeds);
        while (todo.Count > 0)
        {
            int v = todo.Pop();
            if (!found.Add(v) || !byPoint.TryGetValue(v, out var around)) continue;
            foreach (int f in around) if (reached.Add(f)) foreach (int w in faces[f]) todo.Push(w);
        }
        return found;
    }

    static bool Positive(float value) => float.IsFinite(value) && value > 0;
    static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    sealed class Nearest
    {
        readonly List<Hit> hits = new();
        public void Add(Hit hit) => hits.Add(hit);
        public Hit? Best(Func<Hit, bool>? accept)
        {
            foreach (var hit in Ranked(accept, 1)) return hit;
            return null;
        }
        public IEnumerable<Hit> Ranked(Func<Hit, bool>? accept, int limit)
        {
            hits.Sort((a, b) => a.DistancePixels.CompareTo(b.DistancePixels));
            for (int first = 0; first < hits.Count;)
            {
                int end = first + 1;
                float tieLimit = hits[first].DistancePixels + DistanceTiePixels;
                while (end < hits.Count && hits[end].DistancePixels <= tieLimit) end++;
                var tied = hits.GetRange(first, end - first).OrderBy(hit => hit.Depth).ThenBy(hit => hit.Id)
                    .ThenBy(hit => hit.DistancePixels).ThenBy(hit => hit.SegmentT)
                    .ThenBy(hit => hit.ClosestPixel.X).ThenBy(hit => hit.ClosestPixel.Y);
                foreach (var hit in tied) if (accept == null || accept(hit))
                {
                    if (limit-- <= 0) yield break;
                    yield return hit;
                    if (limit == 0) yield break;
                }
                first = end;
            }
        }
    }
}
