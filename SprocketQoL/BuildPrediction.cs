using System.Numerics;

namespace SprocketQoL;

/// A conservative drag assistant, not a learned prediction of a player's intent.
/// All positions and displacement vectors must use the same coordinate system.
/// Projection, visibility, and native axis constraints belong to the caller.
public static class BuildPrediction
{
    [Flags]
    public enum AxisMask { None = 0, X = 1, Y = 2, Z = 4, All = X | Y | Z }

    public readonly record struct Target(int Id, Vector3 Position, bool Visible = true);
    public readonly record struct Projection(Vector2 Pixel, float Depth);
    public readonly record struct Query(Vector3 Anchor, Vector3 RawDelta, AxisMask AllowedAxes = AxisMask.All,
        float RadiusPixels = 12, float MaxCorrection = .1f, float HoldRadiusFactor = 1.35f,
        float SwitchMarginPixels = 1.25f);
    public readonly record struct Hit(int TargetId, Vector3 Delta, Vector3 Correction,
        Vector3 AnchorPosition, Vector3 TargetPosition, AxisMask AllowedAxes, float DistancePixels)
    {
        /// Axis- or plane-constrained suggestions may align only part of a target.
        public bool Coincident => DistanceSquared(AnchorPosition, TargetPosition) <= 1e-12;
    }

    const float TiePixels = .001f;

    /// Resolve only the active grabbed point or selection pivot. The caller applies
    /// Hit.Delta to the entire selection, preserving offsets between its vertices.
    /// Never submit every selected vertex as an independent anchor: a distant vertex
    /// must not unexpectedly pull an entire face toward a different target.
    public static Hit? Resolve(IEnumerable<Target>? targets, Query query, Func<Vector3, Projection>? project,
        Hit? previous = null, Func<Vector3, Vector3>? constrainCorrection = null,
        IReadOnlySet<int>? excluded = null, Func<Hit, bool>? accept = null,
        int? preferredTargetId = null, bool cycleNext = false)
    {
        if (targets == null || project == null || !Valid(query)) return null;
        Vector3 moved = query.Anchor + query.RawDelta;
        if (!Finite(moved)) return null;
        Projection origin = project(moved);
        if (!Valid(origin)) return null;

        double radius = query.RadiusPixels, holdRadius = radius * query.HoldRadiusFactor;
        double maxCorrection2 = (double)query.MaxCorrection * query.MaxCorrection;
        var hits = new List<Hit>();
        Hit? held = null;
        foreach (var target in targets)
        {
            if (!target.Visible || excluded?.Contains(target.Id) == true || !Finite(target.Position)) continue;
            bool retaining = previous is Hit last && last.TargetId == target.Id && last.AllowedAxes == query.AllowedAxes;
            Projection projected = project(target.Position);
            if (!Valid(projected)) continue;
            double distance2 = DistanceSquared(projected.Pixel, origin.Pixel);
            double limit = retaining ? holdRadius : radius;
            if (distance2 > limit * limit) continue;

            Vector3 correction = Mask(target.Position - moved, query.AllowedAxes);
            if (!Finite(correction)) continue;
            if (constrainCorrection != null) correction = constrainCorrection(correction);
            // A constraint callback is not allowed to override explicitly disabled
            // local axes. Runtime callers with rotated constraints use All here.
            correction = Mask(correction, query.AllowedAxes);
            if (!Finite(correction)) continue;
            double correction2 = DistanceSquared(correction, Vector3.Zero);
            if (correction2 > maxCorrection2 || correction2 <= 1e-16 && !retaining) continue;
            Vector3 delta = query.RawDelta + correction, aligned = moved + correction;
            if (!Finite(delta) || !Finite(aligned)) continue;
            Projection alignedProjection = project(aligned);
            if (!Valid(alignedProjection) || DistanceSquared(alignedProjection.Pixel, origin.Pixel) > limit * limit) continue;

            float distance = (float)Math.Sqrt(distance2);
            if (!float.IsFinite(distance)) continue;
            var hit = new Hit(target.Id, delta, correction, aligned, target.Position, query.AllowedAxes,
                distance);
            if (retaining && (held == null || Compare(hit, held.Value) < 0)) held = hit;
            // Expanded release distance is exclusive to the held target. It cannot
            // become a new suggestion after the user's pointer has moved away.
            if (distance2 <= radius * radius) hits.Add(hit);
        }

        // Visibility raycasts can be much more expensive than projection. Delay the
        // callback until after screen-distance/world-distance filtering and ranking,
        // then stop at the first accepted candidate. Cache the held candidate's
        // answer so hysteresis never asks the caller to raycast it twice.
        var accepted = accept == null ? null : new Dictionary<Hit, bool>();
        bool Accept(Hit candidate)
        {
            if (accept == null) return true;
            if (accepted!.TryGetValue(candidate, out bool result)) return result;
            result = accept(candidate);
            accepted[candidate] = result;
            return result;
        }
        if (cycleNext)
        {
            var eligible = Ranked(hits, Accept).Take(32).ToArray();
            if (eligible.Length == 0) return null;
            int current = Array.FindIndex(eligible, h => h.TargetId == (preferredTargetId ?? previous?.TargetId));
            return eligible[(current + 1) % eligible.Length];
        }
        if (preferredTargetId is int pinned)
        {
            foreach (var candidate in hits.Where(h => h.TargetId == pinned)) if (Accept(candidate)) return candidate;
        }
        Hit? best = Best(hits, Accept);
        if (held is Hit keep && (best == null || keep.TargetId == best.Value.TargetId
                || keep.DistancePixels <= best.Value.DistancePixels + query.SwitchMarginPixels) && Accept(keep)) return keep;
        return best;
    }

    static Hit? Best(List<Hit> hits, Func<Hit, bool> accept)
    {
        foreach(var hit in Ranked(hits,accept)) return hit;
        return null;
    }
    static IEnumerable<Hit> Ranked(List<Hit> hits, Func<Hit, bool> accept)
    {
        // Compare with the global minimum, so a chain of subpixel ties never changes
        // the result merely because the caller enumerated the mesh differently.
        hits.Sort((a, b) => a.DistancePixels.CompareTo(b.DistancePixels));
        for (int first = 0; first < hits.Count;)
        {
            int end = first + 1;
            float limit = hits[first].DistancePixels + TiePixels;
            while (end < hits.Count && hits[end].DistancePixels <= limit) end++;
            var tied = hits.GetRange(first, end - first)
                .OrderBy(hit => DistanceSquared(hit.Correction, Vector3.Zero))
                .ThenBy(hit => hit.TargetId)
                .ThenBy(hit => hit.TargetPosition.X).ThenBy(hit => hit.TargetPosition.Y).ThenBy(hit => hit.TargetPosition.Z);
            foreach (var hit in tied) if (accept(hit)) yield return hit;
            first = end;
        }
    }

    /// Closest arbitrary edge point. Degenerate edges reduce to their endpoint.
    public static bool ClosestEdge(Vector3 point, Vector3 a, Vector3 b, out Vector3 closest)
    {
        closest=default;
        if (!Finite(point)||!Finite(a)||!Finite(b)) return false;
        double x=(double)b.X-a.X,y=(double)b.Y-a.Y,z=(double)b.Z-a.Z;
        double length=x*x+y*y+z*z;
        double t=length>1e-24 ? Math.Clamp((((double)point.X-a.X)*x+((double)point.Y-a.Y)*y+((double)point.Z-a.Z)*z)/length,0,1) : 0;
        closest=new((float)(a.X+x*t),(float)(a.Y+y*t),(float)(a.Z+z*t));
        return Finite(closest);
    }

    /// Closest point on a triangle, including its boundary. Degenerate triangles
    /// fall back to their edges rather than producing invalid barycentric weights.
    public static bool ClosestFace(Vector3 point, Vector3 a, Vector3 b, Vector3 c, out Vector3 closest)
    {
        closest=default;
        if (!Finite(point)||!Finite(a)||!Finite(b)||!Finite(c)) return false;
        double Dot(Vector3 u,Vector3 v)=>(double)u.X*v.X+(double)u.Y*v.Y+(double)u.Z*v.Z;
        var ab=b-a;var ac=c-a;var ap=point-a;
        double aa=Dot(ab,ab),bb=Dot(ab,ac),cc=Dot(ac,ac),pa=Dot(ap,ab),pc=Dot(ap,ac);
        double determinant=aa*cc-bb*bb;
        if(double.IsFinite(determinant)&&determinant>Math.Max(1e-30,aa*cc*1e-12))
        {
            double u=(pa*cc-pc*bb)/determinant,v=(pc*aa-pa*bb)/determinant;
            if(u>=0&&v>=0&&u+v<=1)
            {
                closest=new((float)(a.X+u*ab.X+v*ac.X),(float)(a.Y+u*ab.Y+v*ac.Y),(float)(a.Z+u*ab.Z+v*ac.Z));
                return Finite(closest);
            }
        }
        var candidates=new List<Vector3>(3);
        if(ClosestEdge(point,a,b,out var p)) candidates.Add(p);
        if(ClosestEdge(point,b,c,out p)) candidates.Add(p);
        if(ClosestEdge(point,c,a,out p)) candidates.Add(p);
        if(candidates.Count==0)return false;
        closest=candidates.OrderBy(p=>DistanceSquared(point,p)).First();return true;
    }

    static int Compare(Hit a, Hit b)
    {
        int result = a.DistancePixels.CompareTo(b.DistancePixels);
        if (result != 0) return result;
        result = DistanceSquared(a.Correction, Vector3.Zero).CompareTo(DistanceSquared(b.Correction, Vector3.Zero));
        if (result != 0) return result;
        result = a.TargetPosition.X.CompareTo(b.TargetPosition.X);
        if (result != 0) return result;
        result = a.TargetPosition.Y.CompareTo(b.TargetPosition.Y);
        return result != 0 ? result : a.TargetPosition.Z.CompareTo(b.TargetPosition.Z);
    }

    static Vector3 Mask(Vector3 value, AxisMask axes) => new(
        (axes & AxisMask.X) != 0 ? value.X : 0,
        (axes & AxisMask.Y) != 0 ? value.Y : 0,
        (axes & AxisMask.Z) != 0 ? value.Z : 0);
    static bool Valid(Query query) => Finite(query.Anchor) && Finite(query.RawDelta)
        && query.AllowedAxes != AxisMask.None && (query.AllowedAxes & ~AxisMask.All) == 0
        && float.IsFinite(query.RadiusPixels) && query.RadiusPixels >= 0
        && float.IsFinite(query.MaxCorrection) && query.MaxCorrection >= 0
        && float.IsFinite(query.HoldRadiusFactor) && query.HoldRadiusFactor >= 1
        && float.IsFinite(query.SwitchMarginPixels) && query.SwitchMarginPixels >= 0;
    static bool Valid(Projection value) => Finite(value.Pixel) && float.IsFinite(value.Depth) && value.Depth > 0;
    static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    static double DistanceSquared(Vector2 a, Vector2 b)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y;
        return x * x + y * y;
    }
    static double DistanceSquared(Vector3 a, Vector3 b)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
        return x * x + y * y + z * z;
    }
}
