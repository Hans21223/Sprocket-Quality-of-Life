using UnityEngine;
using Num = System.Numerics.Vector3;
using Num2 = System.Numerics.Vector2;

namespace SprocketQoL;

/// Passive wire previews. Uses no scene objects, colliders, physics, cameras or tank materials.
internal sealed class GeometryPreview : IDisposable
{
    const int MaxPreviewSegments = 40_000;
    static Material? lineMaterial;
    readonly List<(Num A, Num B)> segments = new();
    GeometryPreviewMath.Bounds bounds;
    RenderTexture? texture;
    float yaw = -30, pitch = 22;
    internal Texture? Texture => texture;
    internal string Description { get; private set; } = "";

    internal void Set(ObjDocument document, float scale, ObjImportUnits units, ObjUpAxis sourceUp)
    {
        bounds = GeometryPreviewMath.Measure(document, scale, units, sourceUp);
        segments.Clear();
        long corners = document.Meshes.Sum(mesh => mesh.Faces.Sum(face => (long)face.Length));
        long stride = Math.Max(1, (corners + MaxPreviewSegments - 1) / MaxPreviewSegments), cursor = 0;
        foreach (var mesh in document.Meshes)
            foreach (var face in mesh.Faces)
                for (int i = 0; i < face.Length; i++, cursor++)
                    if (cursor % stride == 0)
                    {
                        var a = GeometryPreviewMath.ImportPosition(mesh.Vertices[face[i]], scale, units, sourceUp);
                        var b = GeometryPreviewMath.ImportPosition(mesh.Vertices[face[(i + 1) % face.Length]], scale, units, sourceUp);
                        segments.Add((a, b));
                    }
        var size = bounds.Size;
        Description = $"Size: {size.X:0.###} × {size.Y:0.###} × {size.Z:0.###} m (X / Y / Z)" +
            (stride > 1 ? $"\nWire preview samples {segments.Count:N0} of {corners:N0} polygon edges." : "\nWire preview of imported geometry.");
        Render();
    }

    internal void Orbit(float horizontal, float vertical)
    { yaw = Mathf.Repeat(yaw + horizontal, 360); pitch = Mathf.Clamp(pitch + vertical, -89, 89); Render(); }

    internal void ResetView() { yaw = -30; pitch = 22; Render(); }

    static bool MaterialReady()
    {
        if (lineMaterial != null) return true;
        var shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
        if (shader == null) return false;
        lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        lineMaterial.mainTexture = Texture2D.whiteTexture;
        lineMaterial.SetInt("_SrcBlend", 5); lineMaterial.SetInt("_DstBlend", 10);
        lineMaterial.SetInt("_Cull", 0); lineMaterial.SetInt("_ZWrite", 0); lineMaterial.SetInt("_ZTest", 8);
        return true;
    }

    void Render()
    {
        if (!MaterialReady()) return;
        if (texture == null)
        {
            texture = new RenderTexture(640, 360, 0) { hideFlags = HideFlags.HideAndDontSave };
            texture.Create();
        }
        var previous = RenderTexture.active;
        bool begun = false, pushed = false;
        try
        {
            RenderTexture.active = texture;
            GL.Clear(true, true, new Color(.035f, .045f, .055f, 1));
            GL.PushMatrix(); pushed = true;
            GL.LoadPixelMatrix(0, texture.width, texture.height, 0);
            if (!lineMaterial!.SetPass(0)) return;
            GL.Begin(GL.LINES); begun = true; GL.Color(new Color(.5f, .85f, 1, .8f));
            var viewport = new Num2(texture.width, texture.height);
            foreach (var (a, b) in segments)
            {
                var p = GeometryPreviewMath.Project(a, bounds, yaw, pitch, viewport);
                var q = GeometryPreviewMath.Project(b, bounds, yaw, pitch, viewport);
                GL.Vertex3(p.X, p.Y, 0); GL.Vertex3(q.X, q.Y, 0);
            }
        }
        finally
        {
            if (begun) GL.End();
            if (pushed) GL.PopMatrix();
            RenderTexture.active = previous;
        }
    }

    /// Draw from an OnGUI Repaint callback, using the actual camera and proposed local geometry.
    /// Near-plane clipping prevents exploding lines when the editor camera passes through a preview.
    internal static void DrawWire(Camera camera, IReadOnlyList<Vector3> vertices, IEnumerable<int[]> faces,
        Matrix4x4 localToWorld, Color tint, int maxEdges = 20_000)
    {
        if (Event.current?.type != EventType.Repaint || camera == null || vertices.Count == 0 || maxEdges <= 0 || !MaterialReady()) return;
        bool begun = false, pushed = false;
        try
        {
            GL.PushMatrix(); pushed = true; GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            if (!lineMaterial!.SetPass(0)) return;
            GL.Begin(GL.LINES); begun = true; GL.Color(tint);
            int count = 0;
            var seen = new HashSet<(int, int)>();
            foreach (var face in faces)
                for (int i = 0; i < face.Length; i++)
                {
                    int ai = face[i], bi = face[(i + 1) % face.Length];
                    if (ai < 0 || bi < 0 || ai >= vertices.Count || bi >= vertices.Count || ai == bi || !seen.Add((Math.Min(ai, bi), Math.Max(ai, bi)))) continue;
                    if (++count > maxEdges) return;
                    var a = localToWorld.MultiplyPoint3x4(vertices[ai]);
                    var b = localToWorld.MultiplyPoint3x4(vertices[bi]);
                    var p = camera.WorldToScreenPoint(a); var q = camera.WorldToScreenPoint(b);
                    float near = camera.nearClipPlane * 1.001f;
                    if (p.z < near && q.z < near) continue;
                    if (p.z < near) { a = Vector3.Lerp(a, b, (near - p.z) / (q.z - p.z)); p = camera.WorldToScreenPoint(a); }
                    else if (q.z < near) { b = Vector3.Lerp(b, a, (near - q.z) / (p.z - q.z)); q = camera.WorldToScreenPoint(b); }
                    if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(q.x) || !float.IsFinite(q.y)) continue;
                    GL.Vertex3(p.x, Screen.height - p.y, 0); GL.Vertex3(q.x, Screen.height - q.y, 0);
                }
        }
        finally { if (begun) GL.End(); if (pushed) GL.PopMatrix(); }
    }

    /// Lines in screen pixels (pairs of ends, from the bottom left like Screen), from an OnGUI Repaint callback, `width`
    /// pixels wide. GUI.DrawTexture can't be used for this: the game's build stripped it.
    internal static void DrawScreenLines(IReadOnlyList<Vector2> ends, Color tint, int width = 3)
    {
        if (Event.current?.type != EventType.Repaint || ends.Count < 2 || !MaterialReady()) return;
        bool begun = false, pushed = false;
        try
        {
            GL.PushMatrix(); pushed = true; GL.LoadPixelMatrix(0, Screen.width, Screen.height, 0);
            if (!lineMaterial!.SetPass(0)) return;
            GL.Begin(GL.LINES); begun = true; GL.Color(tint);
            for (int i = 0; i + 1 < ends.Count; i += 2)
            {
                Vector2 a = new(ends[i].x, Screen.height - ends[i].y), b = new(ends[i + 1].x, Screen.height - ends[i + 1].y);
                var side = (b - a).sqrMagnitude > 0 ? new Vector2(a.y - b.y, b.x - a.x).normalized : Vector2.right;
                for (int k = 0; k < width; k++)
                {
                    var o = side * (k - (width - 1) / 2f);
                    GL.Vertex3(a.x + o.x, a.y + o.y, 0); GL.Vertex3(b.x + o.x, b.y + o.y, 0);
                }
            }
        }
        finally { if (begun) GL.End(); if (pushed) GL.PopMatrix(); }
    }

    public void Dispose()
    {
        segments.Clear();
        if (texture != null) { texture.Release(); UnityEngine.Object.Destroy(texture); texture = null; }
    }
}
