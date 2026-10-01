using System.Globalization;
using System.Numerics;

namespace SprocketQoL;

/// Pure drawing operations: no changes to the live gun, paint or vehicle.
internal static class DrawingOptions
{
    internal static float Strength(float percent) => float.IsFinite(percent) ? Math.Clamp(percent / 100, 0, 1) : 1;
    internal static void Ink(byte[] rgb, int q, byte grey, float strength)
    {
        for (int c = 0; c < 3; c++) rgb[q + c] = (byte)MathF.Round(rgb[q + c] * (1 - strength) + grey * strength);
    }
    internal static string Weight(float kg) => float.IsFinite(kg) && kg > 0 ? (kg / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " t" : "";
    internal static byte[] Blueprint(byte[] lines, int width = 0, int height = 0, int gridStep = 0, float gridStrength = 0.2f)
    {
        if (lines.Length % 3 != 0) throw new ArgumentException("Blueprint input must contain complete RGB pixels.");
        if (gridStep > 0 && (width <= 0 || height <= 0 || (long)width * height * 3 != lines.Length))
            throw new ArgumentException("Blueprint grid dimensions must match the image.");
        gridStrength = float.IsFinite(gridStrength) ? Math.Clamp(gridStrength, 0, 1) : 0.2f;
        var blue = new byte[lines.Length];
        byte[] paper = { 19, 55, 91 }, ink = { 230, 245, 255 };
        for (int p = 0; p < lines.Length; p += 3)
        {
            float grid = 0;
            if (gridStep > 0)
            {
                int x = p / 3 % width, y = p / 3 / width;
                bool vertical = x % gridStep == 0, horizontal = y % gridStep == 0;
                if (vertical || horizontal)
                    grid = gridStrength * ((vertical && x / gridStep % 4 == 0 || horizontal && y / gridStep % 4 == 0) ? 1 : 0.45f);
            }
            // Grid is paper decoration beneath the ink, so it cannot darken text or vehicle contours.
            for (int c = 0; c < 3; c++)
            {
                int background = paper[c] + (int)((ink[c] - paper[c]) * grid);
                blue[p + c] = (byte)(background + (ink[c] - background) * (255 - lines[p]) / 255);
            }
        }
        return blue;
    }

    internal sealed record Ghost(int View, Drawing.Shape[] Shapes, Vector3 Pivot, Vector3 Tip, Vector3[] Arc, string Label, bool FullCircle = false);
    internal static List<Ghost> Motion(IReadOnlyList<Drawing.Shape> barrel, Vector3 pivot, Vector3 tip,
        Vector3 right, Vector3 up, float minElevation, float maxElevation, float minTraverse, float maxTraverse, bool elevation, bool traverse)
    {
        var result = new List<Ghost>();
        if (!Finite(pivot) || !Finite(tip)) return result;
        void Add(float degrees, bool vertical)
        {
            if (!float.IsFinite(degrees) || MathF.Abs(degrees) < 0.05f || MathF.Abs(degrees) > 180) return;
            var axis = vertical ? -right : up; // positive elevation raises a +Z gun
            if (!Finite(axis) || axis.LengthSquared() < 0.5f) return;
            axis = Vector3.Normalize(axis);
            Vector3 Turn(Vector3 p, float angle) => pivot + Vector3.Transform(p - pivot, Quaternion.CreateFromAxisAngle(axis, angle * MathF.PI / 180));
            var shapes = barrel.Select(s => Drawing.Weld(s.P.Select(p => Turn(p, degrees)).ToArray(), s.T)).ToArray();
            // The angle arc follows the muzzle's sweep, preserving any offset between the barrel and trunnion.
            int steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(degrees) / 2));
            var arc = Enumerable.Range(0, steps + 1).Select(i => Turn(tip, degrees * i / steps)).ToArray();
            string label = MathF.Abs(degrees).ToString("0.#", CultureInfo.InvariantCulture) + "° " +
                (vertical ? degrees > 0 ? "ELEVATION" : "DEPRESSION" : degrees > 0 ? "RIGHT TRAVERSE" : "LEFT TRAVERSE");
            result.Add(new Ghost(vertical ? 2 : 0, shapes, pivot, Turn(tip, degrees), arc, label));
        }
        if (elevation && minElevation <= maxElevation) { Add(minElevation, true); if (maxElevation != minElevation) Add(maxElevation, true); }
        if (traverse && minTraverse <= maxTraverse) { Add(minTraverse, false); if (maxTraverse != minTraverse) Add(maxTraverse, false); }
        return result;
    }

    internal static List<Ghost> TurretMotion(IReadOnlyList<Drawing.Shape> barrel, Vector3 pivot, Vector3 tip, Vector3 up, float min, float max)
    {
        if (!float.IsFinite(min) || !float.IsFinite(max) || max <= min || min < -360 || max > 360 || !Finite(pivot) || !Finite(tip) || !Finite(up) || up.LengthSquared() < 0.5f) return new();
        up = Vector3.Normalize(up);
        Vector3 Turn(Vector3 p, float angle) => pivot + Vector3.Transform(p - pivot, Quaternion.CreateFromAxisAngle(up, angle * MathF.PI / 180));
        bool full = max - min >= 359.9f;
        int steps = Math.Max(2, (int)MathF.Ceiling(Math.Min(360, max - min) / 2));
        var arc = Enumerable.Range(0, steps + 1).Select(i => Turn(tip, min + Math.Min(360, max - min) * i / steps)).ToArray();
        if (full) return new() { new Ghost(0, Array.Empty<Drawing.Shape>(), pivot, arc[0], arc, "360° TURRET ROTATION", true) };
        Ghost Limit(float degrees, bool first) => new(0,
            barrel.Select(s => Drawing.Weld(s.P.Select(p => Turn(p, degrees)).ToArray(), s.T)).ToArray(), pivot, Turn(tip, degrees),
            first ? arc : Array.Empty<Vector3>(), degrees.ToString("0.#", CultureInfo.InvariantCulture) + "° TURRET " + (first ? "MIN" : "MAX"));
        return new() { Limit(min, true), Limit(max, false) };
    }

    internal static void Segment(byte[] rgb, int w, int h, Vector3 a, Vector3 b, byte grey, bool dashed)
    {
        float length = Vector2.Distance(new(a.X, a.Y), new(b.X, b.Y));
        if (w <= 0 || h <= 0 || !Finite(a) || !Finite(b) || !float.IsFinite(length)) return;
        int count = Math.Max(1, (int)MathF.Min(MathF.Ceiling(length), (long)Math.Max(w, h) * 3));
        for (int i = 0; i <= count; i++)
        {
            if (dashed && (i / 10) % 2 != 0) continue;
            var p = Vector3.Lerp(a, b, i / (float)count);
            int x = (int)MathF.Round(p.X), y = (int)MathF.Round(p.Y);
            if (x >= 0 && y >= 0 && x < w && y < h) Ink(rgb, (y * w + x) * 3, grey, 1);
        }
    }
    internal static void DrawMotion(IReadOnlyList<Ghost> motion, byte[][] sheets, Drawing.View[] views, int w, int h, (int X, int Y)[] at)
    {
        var labels = new List<(int X, int Y, int W, int H)>();
        var masks = new Dictionary<int, bool[]>();
        foreach (var ghost in motion)
        {
            int i = ghost.View;
            var v = views[i];
            if (ghost.Shapes.Length > 0)
            {
                if (!masks.TryGetValue(i, out var ink)) masks[i] = ink = new bool[v.Width * v.Height];
                else Array.Clear(ink, 0, ink.Length);
                Drawing.Lines(ghost.Shapes, v, Drawing.Depths(ghost.Shapes, v), ink, 10);
                for (int y = 0; y < v.Height; y++)
                    for (int x = 0; x < v.Width; x++)
                        if (ink[y * v.Width + x] && at[i].X + x >= 0 && at[i].Y + y >= 0 && at[i].X + x < w && at[i].Y + y < h)
                            foreach (var sheet in sheets) Ink(sheet, ((at[i].Y + y) * w + at[i].X + x) * 3, 70, 0.8f);
            }
            Vector3 OnSheet(Vector3 p) => v.Project(p) + new Vector3(at[i].X, at[i].Y, 0);
            var arc = ghost.Arc.Select(OnSheet).ToArray();
            var tip = ghost.FullCircle && arc.Length > 0 ? arc.Aggregate((a,b) => a.Y >= b.Y ? a : b) : OnSheet(ghost.Tip);
            var pivot = OnSheet(ghost.Pivot);
            var words = Drawing.Words(ghost.Label, Math.Clamp(v.Height - 12, 1, 27), false, Math.Max(1, Math.Min(360, v.Width - 12)));
            int minX = at[i].X + Math.Min(6, v.Width / 2), maxX = Math.Max(minX, at[i].X + v.Width - words.W - 6);
            int minY = at[i].Y + Math.Min(words.H + 6, v.Height - 1), maxY = Math.Max(minY, at[i].Y + v.Height - 6);
            int tx = Math.Clamp((int)tip.X - words.W / 2, minX, maxX);
            int ty = Math.Clamp((int)tip.Y + (tip.Y >= pivot.Y ? words.H + 22 : -22), minY, maxY);
            // Stagger nearby labels, staying within this view's reserved space.
            for (int attempt = 0; attempt < 12 && labels.Any(r => tx < r.X + r.W + 8 && tx + words.W + 8 > r.X && ty > r.Y - r.H - 8 && ty - words.H - 8 < r.Y); attempt++)
                ty = Math.Clamp(ty + (tip.Y >= pivot.Y ? -1 : 1) * (words.H + 12), minY, maxY);
            labels.Add((tx, ty, words.W, words.H));
            foreach (var sheet in sheets)
            {
                if (!ghost.FullCircle) DrawingOptions.Segment(sheet, w, h, pivot, tip, 115, true);
                for (int k = 1; k < arc.Length; k++) DrawingOptions.Segment(sheet, w, h, arc[k - 1], arc[k], 100, false);
                DrawingOptions.Segment(sheet, w, h, tip, new Vector3(Math.Clamp(tip.X, tx, tx + words.W), tip.Y >= ty ? ty + 4 : ty - words.H - 4, 0), 115, false);
                Drawing.Stamp(sheet, w, h, tx, ty, words, 40);
            }
        }
    }

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

}
