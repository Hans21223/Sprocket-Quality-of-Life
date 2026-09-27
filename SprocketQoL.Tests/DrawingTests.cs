using System.IO.Compression;
using System.Numerics;
using SprocketQoL;

/// The drawing sheet's geometry, without the game: a cube shows its 12 edges and not its face diagonals, a box hidden
/// behind it shows nothing, and the PNG reads back as the picture written.
static class DrawingTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception("drawing: " + message); }

    /// A box as a hard-edged mesh does it: every face with its own four corners (24 corners, 12 triangles).
    static (List<Vector3> Points, List<int> Tris) Box(Vector3 centre, float size)
    {
        var points = new List<Vector3>();
        var tris = new List<int>();
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        foreach (var n in axes.SelectMany(a => new[] { a, -a }))
        {
            var u = MathF.Abs(n.X) > 0 ? Vector3.UnitY : Vector3.UnitX;
            var v = Vector3.Cross(n, u);
            int at = points.Count;
            foreach (var (su, sv) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                points.Add(centre + (n + su * u + sv * v) * size / 2);
            tris.AddRange(new[] { at, at + 1, at + 2, at, at + 2, at + 3 });
        }
        return (points, tris);
    }

    public static void Run()
    {
        var (cp, ct) = Box(Vector3.Zero, 1);
        var cube = Drawing.Weld(cp, ct);
        Check(cube.P.Length == 8 && cube.T.Length == 36, $"cube welds to 8 corners, 12 triangles (got {cube.P.Length}, {cube.T.Length / 3})");
        Check(cube.E.Count == 18 && cube.E.Count(e => e.Crease) == 12, $"cube: 18 edges, 12 of them creases (got {cube.E.Count}, {cube.E.Count(e => e.Crease)})");

        // From above, 100 px a metre: the cube's square outline, nothing inside; a small box under it hidden.
        var (bp, bt) = Box(new Vector3(0.2f, -2, 0.2f), 0.2f);
        var hidden = Drawing.Weld(bp, bt);
        var shapes = new[] { cube, hidden };
        var view = new Drawing.View(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY, 100, 140, 140);
        var depth = Drawing.Depths(shapes, view);
        var ink = new bool[140 * 140];
        Drawing.Lines(shapes, view, depth, ink);
        bool At(float x, float y) => ink[(int)y * 140 + (int)x];
        Check(At(20, 70) && At(120, 70) && At(70, 20) && At(70, 120), "the cube's four sides are drawn (its edges at pixels 20 and 120)");
        Check(!At(70, 70) && !At(40, 100), "nothing inside the cube's top (its face diagonals aren't creases)");
        Check(!At(80, 90) && !At(100, 90), "the box under the cube is hidden");
        Check(depth.Z[70 * 140 + 70] < -0.4f && float.IsPositiveInfinity(depth.Z[5 * 140 + 5]) && depth.Face[5 * 140 + 5] == -1, "depth: the cube's top in the middle, nothing in the corner");

        // The backdrop kept to 3 pixels round the cube.
        var all = Enumerable.Repeat(true, 140 * 140).ToArray();
        Drawing.Clip(all, depth.Z, 140, 140, 3);
        Check(all[70 * 140 + 70] && all[70 * 140 + 17] && !all[70 * 140 + 16] && !all[5 * 140 + 5], "clip: the cube and 3 pixels round it");

        // A steep-walled turret from above (its top 5 cm in on each side, over 1 m): the foot of each wall lies far
        // behind the wall a pixel in, and is still drawn all the way round.
        var (fp, ft) = Box(Vector3.Zero, 1);
        for (int i = 0; i < fp.Count; i++) if (fp[i].Y > 0) fp[i] = new Vector3(fp[i].X * 0.9f, fp[i].Y, fp[i].Z * 0.9f);
        var turret = new[] { Drawing.Weld(fp, ft) };
        var foot = new bool[140 * 140];
        Drawing.Lines(turret, view, Drawing.Depths(turret, view), foot);
        // (a sloped shape's corners aren't exact in floats: its lines may fall a pixel short of 20 or 120)
        bool Across(int y, int x) => foot[y * 140 + x] || foot[y * 140 + x - 1];
        bool Up(int y, int x) => foot[y * 140 + x] || foot[(y - 1) * 140 + x];
        var gaps = Enumerable.Range(22, 96).Where(k => !Across(k, 20) || !Across(k, 120) || !Up(20, k) || !Up(120, k)).ToList();
        Check(gaps.Count == 0, $"steep walls: their foot drawn all round (gaps at {string.Join(", ", gaps.Take(8))})");
        Check(foot[70 * 140 + 25] && !foot[70 * 140 + 70], "steep walls: the top's edge drawn too, nothing inside");

        // The outline of a drawn square.
        var solid = new bool[10 * 10];
        for (int y = 2; y < 8; y++) for (int x = 2; x < 8; x++) solid[y * 10 + x] = true;
        var edge = new bool[100];
        Drawing.Outline(solid, 10, 10, edge);
        Check(edge[2 * 10 + 4] && edge[4 * 10 + 7] && !edge[4 * 10 + 4] && !edge[0], "outline: the square's border, not its inside or outside");

        // Writing: text inks inside its box only; a dimension draws its line, end marks and measure under it.
        int tw = 400, th = 200;
        var sheet = Enumerable.Repeat((byte)255, tw * th * 3).ToArray();
        bool Inked(int x, int y) => sheet[(y * tw + x) * 3] == 0;
        Drawing.Text(sheet, tw, th, "TOP 10.5 m", 10, 150, 3, 0);
        Check(Drawing.TextWidth("TOP 10.5 m", 3) == 177 && Drawing.TextHeight(3) == 21, "text size");
        var inked = Enumerable.Range(0, tw * th).Where(p => sheet[p * 3] == 0).ToList();
        Check(inked.Count > 200 && inked.All(p => p % tw >= 10 && p % tw < 10 + 177 && p / tw >= 150 && p / tw < 171), "text inks only inside its box");
        Check(Inked(10, 170) && Inked(22, 170), "T's top bar");
        Drawing.Dimension(sheet, tw, th, 50, 100, 350, 100, "3.00 m", 3, 0);
        Check(Inked(200, 100) && Inked(50, 110) && Inked(350, 90) && !Inked(200, 110), "dimension line and its end marks");
        Check(Enumerable.Range(160, 80).Any(x => Inked(x, 70)), "the measure under the line");
        if (Environment.GetEnvironmentVariable("QOL_DRAWING_PREVIEW") is { Length: > 0 } preview) Drawing.SavePng(preview, tw, th, sheet);

        // Free text as Windows draws it: inked letters, words wrapped to the width, any language; stamped only in its box.
        var one = Drawing.Words("Tiger I  88 mm KwK 36", 40, true, 2000);
        Check(one.W > 100 && one.H >= 40 && one.H < 80 && one.Ink.Max() > 200 && one.Ink.Count(a => a == 0) > one.Ink.Length / 2,
              $"words: one line of inked letters ({one.W} x {one.H})");
        var wrapped = Drawing.Words(string.Join(" ", Enumerable.Repeat("armour", 30)), 30, false, 300);
        Check(wrapped.W <= 300 && wrapped.H > 4 * 30, $"words: long text wraps within the width ({wrapped.W} x {wrapped.H})");
        Check(Drawing.Words("รถถังหนัก", 30, false, 1000).Ink.Max() > 200 && Drawing.Words("", 30, false, 100).W == 0, "words: Thai letters draw; empty text is nothing");
        var page = Enumerable.Repeat((byte)255, 400 * 100 * 3).ToArray();
        Drawing.Stamp(page, 400, 100, 10, 90, one, 0);
        var dark = Enumerable.Range(0, 400 * 100).Where(p => page[p * 3] < 128).ToList();
        Check(dark.Count > 50 && dark.All(p => p % 400 >= 10 && p / 400 <= 90 && p / 400 > 90 - one.H), "stamp: letters inked inside the text's box, from its top down");

        // A 3 x 2 picture written and read back.
        var rgb = Enumerable.Range(0, 3 * 2 * 3).Select(i => (byte)(i * 10)).ToArray();
        var file = Path.Combine(Path.GetTempPath(), "qol-drawing-test.png");
        Drawing.SavePng(file, 3, 2, rgb);
        var bytes = File.ReadAllBytes(file);
        Check(bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
        int idat = FindChunk(bytes, "IDAT", out int length);
        using var unpacked = new ZLibStream(new MemoryStream(bytes, idat, length), CompressionMode.Decompress);
        var raw = new MemoryStream();
        unpacked.CopyTo(raw);
        var rows = raw.ToArray();
        Check(rows.Length == 2 * (1 + 3 * 3), $"PNG rows: 2 of 1 + 9 bytes (got {rows.Length})");
        Check(rows[1] == rgb[9] && rows[11] == rgb[0], "PNG rows go from the top (the picture's bottom row last)");
        File.Delete(file);
        Console.WriteLine("DRAWING_TESTS_OK: cube edges, hidden box, clip, steep walls, outline, PNG");
    }

    static int FindChunk(byte[] png, string type, out int length)
    {
        for (int at = 8; at + 8 <= png.Length;)
        {
            length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            if (System.Text.Encoding.ASCII.GetString(png, at + 4, 4) == type) return at + 8;
            at += 12 + length;
        }
        throw new Exception("drawing: no " + type + " chunk");
    }
}
