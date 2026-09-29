using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace SprocketQoL;

/// The drawing sheet's own work, apart from the game (so it's tested offline): a vehicle's meshes as shapes, the edges a
/// drawing shows (where faces meet at an angle, open edges, and each view's outline of curved parts), which of them a
/// view sees (a depth picture of every face hides the edges behind), and a sheet written as a PNG.
internal static class Drawing
{
    internal const float CreaseDegrees = 25; // faces meeting at more than this show their edge
    internal const float Bias = 0.008f;      // metres an edge may lie behind the nearest face and still show
    static readonly float CreaseCos = MathF.Cos(CreaseDegrees * MathF.PI / 180);

    internal sealed class Shape
    {
        public Vector3[] P = Array.Empty<Vector3>();  // corners, welded
        public int[] T = Array.Empty<int>();           // triangles, three corners each
        public Vector3[] N = Array.Empty<Vector3>();  // each triangle's normal
        public readonly List<(int A, int B, int F1, int F2, bool Crease)> E = new(); // F2 -1: an open edge
        public bool Edges = true; // false: it only hides what's behind it (a crew figure: its outline is the picture's)
        public int ContourGroup = -1; // vehicle part ID; meshes belonging to one part share an outline group
    }

    /// A mesh (world space) as a shape: corners within 0.1 mm made one, so faces that share an edge (even across a
    /// hard-shading seam, where the mesh has two copies of each corner) know each other.
    internal static Shape Weld(IReadOnlyList<Vector3> points, IReadOnlyList<int> triangles)
    {
        var index = new Dictionary<(long, long, long), int>();
        var corners = new List<Vector3>();
        var map = new int[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var key = ((long)MathF.Round(p.X * 10000), (long)MathF.Round(p.Y * 10000), (long)MathF.Round(p.Z * 10000));
            if (!index.TryGetValue(key, out int at)) { at = corners.Count; index[key] = at; corners.Add(p); }
            map[i] = at;
        }
        var tris = new List<int>();
        var normals = new List<Vector3>();
        for (int t = 0; t + 2 < triangles.Count; t += 3)
        {
            int a = map[triangles[t]], b = map[triangles[t + 1]], c = map[triangles[t + 2]];
            if (a == b || b == c || a == c) continue;
            var n = Vector3.Cross(corners[b] - corners[a], corners[c] - corners[a]);
            if (n.LengthSquared() < 1e-14f) continue;
            tris.Add(a); tris.Add(b); tris.Add(c);
            normals.Add(Vector3.Normalize(n));
        }
        var shape = new Shape { P = corners.ToArray(), T = tris.ToArray(), N = normals.ToArray() };
        var faces = new Dictionary<long, (int F1, int F2, int Count)>();
        for (int f = 0; f < shape.N.Length; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = shape.T[f * 3 + k], b = shape.T[f * 3 + (k + 1) % 3];
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                faces[key] = faces.TryGetValue(key, out var e) ? (e.F1, e.Count == 1 ? f : e.F2, e.Count + 1) : (f, -1, 1);
            }
        foreach (var (key, e) in faces)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xffffffff);
            bool open = e.Count != 2; // an open edge, or more than two faces on it
            shape.E.Add((a, b, e.F1, open ? -1 : e.F2, open || Vector3.Dot(shape.N[e.F1], shape.N[e.F2]) < CreaseCos));
        }
        return shape;
    }

    /// One orthographic view: its centre, the sheet's right and up, the way it looks, pixels per metre, and its size.
    internal readonly struct View
    {
        public readonly Vector3 Centre, Right, Up, Look;
        public readonly float Scale;
        public readonly int Width, Height;

        public View(Vector3 centre, Vector3 right, Vector3 up, Vector3 look, float scale, int width, int height)
        {
            Centre = centre; Right = right; Up = up; Look = look; Scale = scale; Width = width; Height = height;
        }

        /// Pixel across, pixel up (from the bottom left) and depth along the view, in metres.
        public Vector3 Project(Vector3 p)
        {
            var q = p - Centre;
            return new Vector3(Width * 0.5f + Vector3.Dot(q, Right) * Scale, Height * 0.5f + Vector3.Dot(q, Up) * Scale, Vector3.Dot(q, Look));
        }
    }

    /// The nearest face at each pixel: its depth (infinity where there's none) and which face it is (every shape's
    /// triangles counted in turn; -1 where there's none).
    internal static (float[] Z, int[] Face) Depths(IReadOnlyList<Shape> shapes, View v)
    {
        var depth = new float[v.Width * v.Height];
        var face = new int[depth.Length];
        Array.Fill(depth, float.PositiveInfinity);
        Array.Fill(face, -1);
        int first = 0;
        foreach (var s in shapes)
        {
            var p = s.P.Select(v.Project).ToArray();
            for (int t = 0; t < s.T.Length; t += 3) Fill(depth, face, first + t / 3, v.Width, v.Height, p[s.T[t]], p[s.T[t + 1]], p[s.T[t + 2]]);
            first += s.N.Length;
        }
        return (depth, face);
    }

    static void Fill(float[] depth, int[] face, int id, int w, int h, Vector3 a, Vector3 b, Vector3 c)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y);
        if (MathF.Abs(area) < 1e-6f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        const float Edge = -1e-4f;
        for (int y = y0; y <= y1; y++)
        {
            float py = y + 0.5f;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float wa = ((b.X - px) * (c.Y - py) - (c.X - px) * (b.Y - py)) / area;
                float wb = ((c.X - px) * (a.Y - py) - (a.X - px) * (c.Y - py)) / area;
                float wc = 1 - wa - wb;
                if (wa < Edge || wb < Edge || wc < Edge) continue;
                float d = wa * a.Z + wb * b.Z + wc * c.Z;
                int i = y * w + x;
                if (d < depth[i]) { depth[i] = d; face[i] = id; }
            }
        }
    }

    /// The edges a view shows, drawn into `ink`: creases and open edges, plus the outline of curved surfaces (an edge
    /// between a face turned to the view and one turned away), where nothing nearer covers them.
    internal static void Lines(IReadOnlyList<Shape> shapes, View v, (float[] Z, int[] Face) depth, bool[] ink, int dashPixels = 0)
    {
        int first = 0;
        foreach (var s in shapes)
        {
            if (!s.Edges) { first += s.N.Length; continue; }
            var p = s.P.Select(v.Project).ToArray();
            foreach (var (a, b, f1, f2, crease) in s.E)
            {
                bool outline = f2 >= 0 && MathF.Sign(Vector3.Dot(s.N[f1], v.Look)) != MathF.Sign(Vector3.Dot(s.N[f2], v.Look));
                if (crease || outline) Line(depth, ink, v.Width, v.Height, s, first, p[a], p[b], a, b, f1, f2, dashPixels);
            }
            first += s.N.Length;
        }
    }

    /// Face `f` of `s` is the edge's own: one of its two faces, or one on the same surface touching an end (a quad's
    /// other half covers the pixels by the edge near that end).
    static bool Own(Shape s, int f, int a, int b, int f1, int f2)
    {
        if (f < 0 || f >= s.N.Length) return false;
        if (f == f1 || f == f2) return true;
        int t = f * 3;
        bool touches = s.T[t] == a || s.T[t + 1] == a || s.T[t + 2] == a || s.T[t] == b || s.T[t + 1] == b || s.T[t + 2] == b;
        return touches && (Vector3.Dot(s.N[f], s.N[f1]) > CreaseCos || f2 >= 0 && Vector3.Dot(s.N[f], s.N[f2]) > CreaseCos);
    }

    static void Line((float[] Z, int[] Face) depth, bool[] ink, int w, int h, Shape s, int first, Vector3 pa, Vector3 pb, int a, int b, int f1, int f2, int dashPixels = 0)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(pb.X - pa.X), MathF.Abs(pb.Y - pa.Y)) * 2));
        for (int i = 0; i <= steps; i++)
        {
            if (dashPixels > 0 && (int)(i / (float)steps * Vector2.Distance(new(pa.X, pa.Y), new(pb.X, pb.Y)) / dashPixels) % 2 != 0) continue;
            var q = Vector3.Lerp(pa, pb, i / (float)steps);
            int x = (int)q.X, y = (int)q.Y;
            if (x < 0 || y < 0 || x >= w || y >= h) continue;
            // Shown where one of its own faces is the nearest round the pixel (the foot of a steep face lies well behind
            // that face a pixel in), or where nothing is nearer, give or take a pixel (an edge on another part's surface).
            float nearest = float.PositiveInfinity;
            bool own = false;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int k = yy * w + xx;
                    nearest = MathF.Min(nearest, depth.Z[k]);
                    own |= Own(s, depth.Face[k] - first, a, b, f1, f2);
                }
            if (own || q.Z <= nearest + Bias) ink[y * w + x] = true;
        }
    }

    /// `solid` kept to within `reach` pixels of a face: whatever the game drew behind the vehicle left out.
    internal static void Clip(bool[] solid, float[] z, int w, int h, int reach)
    {
        var across = new bool[solid.Length]; // a face within reach along the row
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int d = -reach; d <= reach && !across[y * w + x]; d++)
                    across[y * w + x] = x + d >= 0 && x + d < w && !float.IsPositiveInfinity(z[y * w + x + d]);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool near = false;
                for (int d = -reach; d <= reach && !near; d++) near = y + d >= 0 && y + d < h && across[(y + d) * w + x];
                if (!near) solid[y * w + x] = false;
            }
    }

    /// The outline of what's drawn (`solid`): each drawn pixel next to an empty one.
    internal static void Outline(bool[] solid, int w, int h, bool[] ink)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (solid[y * w + x] && (x == 0 || y == 0 || x == w - 1 || y == h - 1 ||
                    !solid[y * w + x - 1] || !solid[y * w + x + 1] || !solid[(y - 1) * w + x] || !solid[(y + 1) * w + x]))
                    ink[y * w + x] = true;
    }

    /// Clean technical contours from the frontmost surfaces, not triangle edges. Show part boundaries,
    /// genuine depth steps and sharp bends; ignore coplanar splits, reversed winding and shallow facets.
    internal static void CleanContours(IReadOnlyList<Shape> shapes, View v, (float[] Z, int[] Face) depth, bool[] ink)
    {
        int count = shapes.Sum(s => s.N.Length), at = 0;
        var normals = new Vector3[count];
        var groups = new long[count];
        var enabled = new bool[count];
        for (int s = 0; s < shapes.Count; s++)
            foreach (var normal in shapes[s].N)
            {
                normals[at] = normal;
                groups[at] = shapes[s].ContourGroup >= 0 ? shapes[s].ContourGroup : -1L - s;
                enabled[at++] = shapes[s].Edges;
            }
        var clean = new bool[ink.Length];
        float step = 1 / v.Scale, jump = MathF.Max(0.002f, step * 0.75f);
        float sharp = MathF.Cos(50 * MathF.PI / 180);
        void Compare(int p, int q, Vector3 across)
        {
            int a = depth.Face[p], b = depth.Face[q];
            // The captured silhouette is added separately, including unreadable parts.
            if (a < 0 || b < 0 || a == b || !enabled[a] || !enabled[b]) return;
            var delta = across * step + v.Look * (depth.Z[q] - depth.Z[p]);
            bool part = groups[a] != groups[b];
            // Both surfaces must see a step off their planes: a steep continuous face is not a depth break.
            bool gap = MathF.Abs(Vector3.Dot(normals[a], delta)) > jump &&
                       MathF.Abs(Vector3.Dot(normals[b], delta)) > jump;
            bool corner = MathF.Abs(Vector3.Dot(normals[a], normals[b])) < sharp;
            if (part || gap || corner) clean[depth.Z[p] <= depth.Z[q] ? p : q] = true;
        }
        for (int y = 0; y < v.Height; y++)
            for (int x = 0; x < v.Width; x++)
            {
                int p = y * v.Width + x;
                if (x + 1 < v.Width) Compare(p, p + 1, v.Right);
                if (y + 1 < v.Height) Compare(p, p + v.Width, v.Up);
            }
        // Subpixel hardware and grazing triangles can make isolated dots. Keep connected contours of 5+ pixels.
        var visited = new bool[clean.Length];
        var component = new List<int>();
        for (int p = 0; p < clean.Length; p++)
        {
            if (!clean[p] || visited[p]) continue;
            component.Clear(); component.Add(p); visited[p] = true;
            for (int k = 0; k < component.Count; k++)
            {
                int x = component[k] % v.Width, y = component[k] / v.Width;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= v.Width || yy >= v.Height) continue;
                        int q = yy * v.Width + xx;
                        if (clean[q] && !visited[q]) { visited[q] = true; component.Add(q); }
                    }
            }
            if (component.Count >= 5) foreach (int q in component) ink[q] = true;
        }
    }

    // ---------- writing on the sheet: a filled box, text, dimensions ----------

    /// A filled box in grey `g` (pixels from the bottom left, both corners included), cut to the picture.
    internal static void Box(byte[] rgb, int w, int h, int x0, int y0, int x1, int y1, byte g)
    {
        for (int y = Math.Max(0, Math.Min(y0, y1)); y <= Math.Min(h - 1, Math.Max(y0, y1)); y++)
            for (int x = Math.Max(0, Math.Min(x0, x1)); x <= Math.Min(w - 1, Math.Max(x0, x1)); x++)
            {
                int q = (y * w + x) * 3;
                rgb[q] = g; rgb[q + 1] = g; rgb[q + 2] = g;
            }
    }

    // A 5 x 7 pixel font, just the characters the sheet writes (rows from the top, # inked).
    static readonly Dictionary<char, string[]> Font = new()
    {
        ['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." },
        ['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" },
        ['3'] = new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." },
        ['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." },
        ['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##.." },
        ['m'] = new[] { ".....", ".....", "##.#.", "#.#.#", "#.#.#", "#...#", "#...#" },
        ['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" },
        ['D'] = new[] { "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####." },
        ['E'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" },
        ['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." },
        ['I'] = new[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." },
        ['N'] = new[] { "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#" },
        ['O'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." },
        ['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." },
        ['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" },
        ['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." },
        ['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." },
    };

    internal static int TextWidth(string text, int size) => Math.Max(0, text.Length * 6 * size - size);
    internal static int TextHeight(int size) => 7 * size;

    /// `text` with its bottom left corner at (x, y), each font pixel `size` pixels square. Unknown characters are spaces.
    internal static void Text(byte[] rgb, int w, int h, string text, int x, int y, int size, byte g)
    {
        foreach (char c in text)
        {
            if (Font.TryGetValue(c, out var rows))
                for (int r = 0; r < 7; r++)
                    for (int k = 0; k < 5; k++)
                        if (rows[r][k] == '#')
                            Box(rgb, w, h, x + k * size, y + (6 - r) * size, x + (k + 1) * size - 1, y + (7 - r) * size - 1, g);
            x += 6 * size;
        }
    }

    /// A dimension, as drawings show one: a line from (x0, y0) to (x1, y1), across or up, with a mark at each end, and
    /// its measure written under it (across) or to its left (up).
    internal static void Dimension(byte[] rgb, int w, int h, int x0, int y0, int x1, int y1, string text, int size, byte g)
    {
        const int Mark = 12;
        if (y0 == y1)
        {
            Box(rgb, w, h, x0, y0, x1, y0 + 1, g);
            Box(rgb, w, h, x0, y0 - Mark, x0 + 1, y0 + Mark, g);
            Box(rgb, w, h, x1 - 1, y0 - Mark, x1, y0 + Mark, g);
            Text(rgb, w, h, text, (x0 + x1 - TextWidth(text, size)) / 2, y0 - Mark - 8 - TextHeight(size), size, g);
        }
        else
        {
            Box(rgb, w, h, x0, y0, x0 + 1, y1, g);
            Box(rgb, w, h, x0 - Mark, y0, x0 + Mark, y0 + 1, g);
            Box(rgb, w, h, x0 - Mark, y1 - 1, x0 + Mark, y1, g);
            Text(rgb, w, h, text, x0 - Mark - 10 - TextWidth(text, size), (y0 + y1 - TextHeight(size)) / 2, size, g);
        }
    }

    // ---------- free text (names, descriptions: any letters, any language) ----------

    /// `text` as Windows draws it (its own fonts, so any language shows, with the words wrapped to `maxWidth`): how much
    /// each pixel is inked, 0 to 255, rows from the top. Empty text gives a 0 x 0 picture.
    internal static (int W, int H, byte[] Ink) Words(string text, int pixelHeight, bool bold, int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0, Array.Empty<byte>());
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), font = IntPtr.Zero, bitmap = IntPtr.Zero;
        IntPtr oldFont = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        try
        {
            font = CreateFontW(-pixelHeight, 0, 0, 0, bold ? 700 : 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI"); // 4: anti-aliased, not ClearType
            oldFont = SelectObject(dc, font);
            const uint Wrap = 0x10, NoPrefix = 0x800, Tabs = 0x40, Measure = 0x400;
            var box = new Rect { Right = maxWidth };
            DrawTextW(dc, text, -1, ref box, Wrap | NoPrefix | Tabs | Measure);
            int w = Math.Max(1, box.Right), h = Math.Max(1, box.Bottom);
            var info = new BitmapInfo { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 }; // -h: rows from the top
            bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException("Windows couldn't make a picture for the text");
            oldBitmap = SelectObject(dc, bitmap);
            SetTextColor(dc, 0xFFFFFF);
            SetBkMode(dc, 1); // transparent: white letters on the picture's black
            var area = new Rect { Right = w, Bottom = h };
            DrawTextW(dc, text, -1, ref area, Wrap | NoPrefix | Tabs);
            var bgra = new byte[w * h * 4];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            var ink = new byte[w * h];
            for (int i = 0; i < ink.Length; i++) ink[i] = Math.Max(bgra[i * 4], Math.Max(bgra[i * 4 + 1], bgra[i * 4 + 2]));
            return (w, h, ink);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(dc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (oldFont != IntPtr.Zero) SelectObject(dc, oldFont);
            if (font != IntPtr.Zero) DeleteObject(font);
            if (dc != IntPtr.Zero) DeleteDC(dc);
        }
    }

    /// A Words picture onto the sheet in grey `g`, its top left corner at (x, top) (sheet rows from the bottom).
    internal static void Stamp(byte[] rgb, int w, int h, int x, int top, (int W, int H, byte[] Ink) words, byte g)
    {
        for (int row = 0; row < words.H; row++)
        {
            int y = top - row;
            if (y < 0 || y >= h) continue;
            for (int col = 0; col < words.W; col++)
            {
                int sx = x + col, a = words.Ink[row * words.W + col];
                if (a == 0 || sx < 0 || sx >= w) continue;
                int q = (y * w + sx) * 3;
                for (int c = 0; c < 3; c++) rgb[q + c] = (byte)((rgb[q + c] * (255 - a) + g * a) / 255);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant, Colours;
    }

    [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc, IntPtr what);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr what);
    [DllImport("gdi32")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
    [DllImport("gdi32")] static extern uint SetTextColor(IntPtr dc, uint colour);
    [DllImport("gdi32")] static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr dc, string text, int length, ref Rect rect, uint format);

    /// An RGB picture (three bytes a pixel, rows from the bottom) as a PNG file.
    internal static void SavePng(string path, int w, int h, byte[] rgb)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var head = new byte[13];
        BigEndian(head, 0, w); BigEndian(head, 4, h);
        head[8] = 8; head[9] = 2; // 8 bits, RGB
        Chunk(file, "IHDR", head);
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
            for (int y = h - 1; y >= 0; y--) { z.WriteByte(0); z.Write(rgb, y * w * 3, w * 3); } // PNG rows go from the top
        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc(Crc(0xffffffff, head, 4, 4), data, 0, data.Length) ^ 0xffffffff;
        var tail = new byte[4];
        BigEndian(tail, 0, (int)crc);
        s.Write(tail);
    }

    static void BigEndian(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(uint crc, byte[] data, int from, int count)
    {
        for (int i = from; i < from + count; i++) crc = CrcTable[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
        return crc;
    }
}
