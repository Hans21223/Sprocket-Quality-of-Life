using System.Numerics;

namespace SprocketQoL;

public enum ObjImportUnits { Metres, Centimetres, Millimetres }
public enum ObjUpAxis { Y, Z, X }

public static class GeometryPreviewMath
{
    public readonly record struct Bounds(Vector3 Min, Vector3 Max)
    {
        public Vector3 Size => Max - Min;
        public Vector3 Centre => Min + Size * .5f;
        public float Radius => Size.Length() * .5f;
    }

    public static float MetresPerUnit(ObjImportUnits units) => units switch
    {
        ObjImportUnits.Metres => 1,
        ObjImportUnits.Centimetres => .01f,
        ObjImportUnits.Millimetres => .001f,
        _ => throw new ArgumentOutOfRangeException(nameof(units))
    };

    /// A proper rotation into the OBJ exporter's Y-up frame; winding and handedness stay unchanged.
    public static Vector3 Orient(Vector3 point, ObjUpAxis sourceUp) => sourceUp switch
    {
        ObjUpAxis.Y => point,
        ObjUpAxis.Z => new(point.X, point.Z, -point.Y),
        ObjUpAxis.X => new(-point.Y, point.X, point.Z),
        _ => throw new ArgumentOutOfRangeException(nameof(sourceUp))
    };

    public static Vector3 ImportPosition(Vector3 source, float scale = 1,
        ObjImportUnits units = ObjImportUnits.Metres, ObjUpAxis sourceUp = ObjUpAxis.Y)
    {
        if (!float.IsFinite(scale) || scale <= 0 || scale > 1000)
            throw new ArgumentOutOfRangeException(nameof(scale), "OBJ scale must be greater than zero and at most 1000.");
        var p = Orient(source, sourceUp) * (scale * MetresPerUnit(units));
        // The game's local mesh frame reflects OBJ X. Its face winding is reversed by the importer.
        p.X = -p.X;
        if (!Finite(p) || Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) > ObjMeshFormat.MaxCoordinate)
            throw new FormatException("Imported vertices must be finite and within +/- 1,000,000 metres.");
        return p;
    }

    public static Bounds Measure(ObjDocument document, float scale = 1,
        ObjImportUnits units = ObjImportUnits.Metres, ObjUpAxis sourceUp = ObjUpAxis.Y)
    {
        ArgumentNullException.ThrowIfNull(document);
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        int count = 0;
        foreach (var mesh in document.Meshes)
            foreach (var point in mesh.Vertices)
            {
                var p = ImportPosition(point, scale, units, sourceUp);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p); count++;
            }
        if (count == 0) throw new FormatException("The model has no vertices to preview.");
        return new(min, max);
    }

    /// Projects actual geometry into a preview rectangle after orbiting about its shared bounds centre.
    public static Vector2 Project(Vector3 point, Bounds bounds, float yawDegrees, float pitchDegrees, Vector2 viewport)
    {
        if (!Finite(point) || !float.IsFinite(yawDegrees) || !float.IsFinite(pitchDegrees) ||
            !float.IsFinite(viewport.X) || !float.IsFinite(viewport.Y) || viewport.X <= 0 || viewport.Y <= 0)
            throw new ArgumentException("Preview projection needs finite geometry and a positive viewport.");
        var rotation = Quaternion.CreateFromYawPitchRoll(yawDegrees * MathF.PI / 180, pitchDegrees * MathF.PI / 180, 0);
        var p = Vector3.Transform(point - bounds.Centre, rotation);
        float zoom = Math.Min(viewport.X, viewport.Y) * .43f / Math.Max(bounds.Radius, .000001f);
        return new(viewport.X * .5f + p.X * zoom, viewport.Y * .5f - p.Y * zoom);
    }

    public static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
}
