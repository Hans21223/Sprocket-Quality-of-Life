using System.Globalization;
using System.Numerics;
using SprocketQoL;

static class ObjMeshFormatTests
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("OBJ format: " + why); }
    static void Bad(string text, string why)
    {
        bool rejected = false;
        try { ObjMeshFormat.Read(text); } catch (FormatException) { rejected = true; }
        Check(rejected, why);
    }
    static void BadExport(ObjDocument document, string why)
    {
        bool rejected = false;
        try { ObjMeshFormat.Write(document); } catch (ArgumentException) { rejected = true; }
        Check(rejected, why);
    }
    const string Triangle = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n";
    static ObjMeshData Mesh(string name = "Hull") => new(name,
        new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, new[] { new[] { 0, 1, 2 } });

    public static void Run()
    {
        checks = 0;
        var basic = ObjMeshFormat.Read(Triangle);
        Check(basic.Meshes.Length == 1 && basic.Meshes[0].Vertices.Length == 3, "plain triangle imports as one mesh");
        Check(basic.Meshes[0].Faces[0].SequenceEqual(new[] { 0, 1, 2 }), "faces use zero-based local indices");
        Check(basic.Meshes[0].Vertices[1] == Vector3.UnitX && basic.Meshes[0].Vertices[2] == Vector3.UnitY,
            "codec does not mirror axes or reverse winding");
        Check(basic.Meshes[0].TexCoords == null && basic.Meshes[0].Normals == null,
            "missing optional vertex attributes stay missing");

        var negative = ObjMeshFormat.Read("v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\nv 0 0 9\n");
        Check(negative.Meshes[0].Vertices.SequenceEqual(basic.Meshes[0].Vertices),
            "negative indices resolve against data at the face, not the end of the file");
        Check(negative.Meshes[0].Vertices.Length == 3, "unreferenced position does not pollute imported polygon bounds");
        var streams = ObjMeshFormat.Read("""
            mtllib never-open-this-file.mtl
            o Hull
            v 0 0 0
            v 1 0 0
            v 0 1 0
            vt 0 0
            vt 0.5 0
            vt 0 1
            vn 0 0 1
            usemtl tank paint
            f -3/-3/-1 -2/-2/-1 -1/-1/-1
            g Tracks
            f 1//1 3//1 2//1
            o Turret
            v 0 0 1
            usemtl steel
            f 1/1/1 2/2/1 4/3/1
            """);
        Check(streams.Meshes.Select(m => m.Name).SequenceEqual(new[] { "Hull", "Hull / Tracks", "Turret" }),
            "objects and groups retain separate polygon batches");
        Check(streams.Meshes[2].Vertices[2] == Vector3.UnitZ, "objects can reference globally declared positions");
        Check(streams.Meshes[0].TexCoords!.SequenceEqual(new[] { Vector2.Zero, new Vector2(0.5f, 0), Vector2.UnitY }),
            "texture coordinates retain their values");
        Check(streams.Meshes[0].Normals!.Single() == Vector3.UnitZ, "shared normals remain shared");
        Check(streams.Meshes[0].FaceCorners![0][2] == new ObjCorner(2, 2, 0), "separate position UV and normal corner indices");
        Check(streams.Meshes[1].TexCoords == null && streams.Meshes[1].FaceCorners![0][1].Normal == 0,
            "normal-only corners do not invent texture coordinates");
        Check(streams.Meshes[0].FaceMaterials![0] == "tank paint" && streams.Meshes[2].FaceMaterials![0] == "steel",
            "material names, including spaces, follow their faces");
        string written = ObjMeshFormat.Write(streams, "Tank.mtl");
        Check(written.Contains("mtllib Tank.mtl\n"), "material library is exported as a reference without file IO");
        Check(written.Contains("f 4//2 5//2 6//2") && written.Contains("f 7/4/3 8/5/3 9/6/3"),
            "all OBJ position UV and normal streams receive global offsets");
        var roundtrip = ObjMeshFormat.Read(written);
        for (int m = 0; m < streams.Meshes.Length; m++)
        {
            var before = streams.Meshes[m]; var after = roundtrip.Meshes[m];
            Check(before.Name == after.Name && before.Vertices.SequenceEqual(after.Vertices), "object names and coordinates survive roundtrip");
            Check(before.Faces.SelectMany(f => f).SequenceEqual(after.Faces.SelectMany(f => f)), "polygon order and winding survive roundtrip");
            Check(before.FaceCorners!.SelectMany(f => f).SequenceEqual(after.FaceCorners!.SelectMany(f => f)), "independent corner attributes survive roundtrip");
            Check(before.FaceMaterials!.SequenceEqual(after.FaceMaterials!), "materials survive roundtrip");
        }

        var first = Mesh("Painted") with { FaceMaterials = new[] { "green" } };
        var second = Mesh("Unpainted");
        var resetMaterial = ObjMeshFormat.Read(ObjMeshFormat.Write(new(new[] { first, second })));
        Check(resetMaterial.Meshes[1].FaceMaterials![0] == "", "an unassigned material does not inherit the previous object's material");
        var seam = Mesh() with
        {
            TexCoords = new[] { Vector2.Zero, Vector2.One, Vector2.UnitY, new Vector2(0.2f, 0.3f) },
            Normals = new[] { Vector3.UnitZ, -Vector3.UnitZ },
            Faces = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 } },
            FaceCorners = new[] { new[] { new ObjCorner(0, 0, 0), new ObjCorner(1, 1, 0), new ObjCorner(2, 2, 0) },
                new[] { new ObjCorner(0, 3, 1), new ObjCorner(2, 2, 1), new ObjCorner(1, 1, 1) } }
        };
        var seamAgain = ObjMeshFormat.Read(ObjMeshFormat.Write(new(new[] { seam }))).Meshes.Single();
        Check(seamAgain.Vertices.Length == 3 && seamAgain.TexCoords!.Length == 4 && seamAgain.Normals!.Length == 2,
            "UV and hard-normal seams do not duplicate mesh positions");
        Check(seamAgain.FaceCorners![1][0] == new ObjCorner(0, 3, 1), "same position can have different UV and normal on another face");

        var polygon = ObjMeshFormat.Read("v 0 0 0\nv 2 0 0\nv 2 2 0\nv 1 1 0\nv 0 2 0\nf 1 2 3 4 5");
        Check(polygon.Meshes[0].Faces.Single().Length == 5, "concave n-gons are retained for importer triangulation");
        Check(ObjMeshFormat.Read(ObjMeshFormat.Write(polygon)).Meshes[0].Faces[0].Length == 5,
            "writer does not silently fan-triangulate n-gons");
        var extended = ObjMeshFormat.Read("\uFEFF# comment\r\no My tank\r\nv 0 0 0 1 0 0 # colour\r\nv 1 0 0 1\r\nv 0 1 0\r\nvt .5\r\nvt 1 .25 0\r\ns 1\r\nl 1 2\r\nunknown ignored\r\nf 1/1 2/2 3/1 # triangle\r\n");
        Check(extended.Meshes[0].Name == "My tank" && extended.Meshes[0].TexCoords![0] == new Vector2(0.5f, 0),
            "BOM CRLF whitespace comments vertex colours and one-dimensional UVs are accepted");
        Check(extended.Meshes[0].TexCoords![1] == new Vector2(1, 0.25f), "optional UV third component is harmless");

        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            var fractions = Mesh() with { Vertices = new[] { new Vector3(0.125f, -0.25f, 0.000001f), Vector3.UnitX, Vector3.UnitY } };
            string french = ObjMeshFormat.Write(new(new[] { fractions }));
            Check(french.Contains("v 0.125 -0.25 1E-06") && !french.Contains("0,125"), "OBJ always uses invariant decimals and exponents");
            Check(ObjMeshFormat.Read(french).Meshes[0].Vertices.SequenceEqual(fractions.Vertices), "fractional coordinates roundtrip under a non-English culture");
        }
        finally { CultureInfo.CurrentCulture = priorCulture; CultureInfo.CurrentUICulture = priorUiCulture; }

        foreach (string face in new[] { "f 0 2 3", "f 1 2 4", "f -4 -2 -1", "f 1 2", "f 1 2 2", "f 1/0 2 3",
            "f 1/1 2 3", "f 1//1 2 3", "f 1/ 2 3", "f 1// 2 3", "f /1 2 3", "f 1/2/3/4 2 3", "f 2147483648 2 3" })
            Bad(Triangle[..Triangle.IndexOf('f')] + face, "invalid corner or face is rejected: " + face);
        foreach (string coordinate in new[] { "NaN", "Infinity", "-Infinity", "1e100", "1000001", "0,25", "oops" })
            Bad(Triangle.Replace("v 0 0 0", "v " + coordinate + " 0 0"), "unsafe or locale-dependent vertex rejected: " + coordinate);
        Bad("v 0 0\n" + Triangle, "incomplete vertex cannot be silently ignored");
        Bad("vn 0 0\n" + Triangle, "incomplete normal rejected");
        Bad("vt 0 1 2 3\n" + Triangle, "invalid UV tuple rejected");
        Bad("vn 0 0 NaN\n" + Triangle, "non-finite normal rejected even when unused");
        Bad("vt NaN 0\n" + Triangle, "non-finite UV rejected even when unused");
        Bad("usemtl\n" + Triangle, "missing material name rejected");
        Bad("v 0 0 0\nl 1 1\n", "a line-only OBJ cannot be imported as a polygon tank");
        Bad("f 1 2 3\n" + Triangle, "faces cannot reference vertices not yet declared");
        Bad(new string(' ', ObjMeshFormat.MaxLineLength + 1) + "\n" + Triangle, "oversized input line is bounded before token allocation");
        string largeFace = string.Join("\n", Enumerable.Range(0, ObjMeshFormat.MaxFaceVertices + 1).Select(i => $"v {i} 0 0")) +
            "\nf " + string.Join(" ", Enumerable.Range(1, ObjMeshFormat.MaxFaceVertices + 1));
        Bad(largeFace, "pathological polygon size is bounded");
        BadExport(new(Array.Empty<ObjMeshData>()), "empty document rejected");
        BadExport(new(new[] { Mesh() with { Vertices = new[] { new Vector3(float.NaN, 0, 0), Vector3.UnitX, Vector3.UnitY } } }), "writer rejects non-finite position");
        BadExport(new(new[] { Mesh() with { Faces = new[] { new[] { 0, 1, 9 } } } }), "writer validates indices before export");
        BadExport(new(new[] { Mesh() with { FaceMaterials = Array.Empty<string>() } }), "writer rejects material count mismatch");
        BadExport(new(new[] { Mesh() with { FaceCorners = new[] { new[] { new ObjCorner(0), new ObjCorner(1) } } } }), "writer rejects corner count mismatch");
        BadExport(new(new[] { Mesh() with { FaceCorners = new[] { new[] { new ObjCorner(0, 0), new ObjCorner(1), new ObjCorner(2) } } } }), "writer rejects UV index without UV data");
        BadExport(new(new[] { Mesh() with { FaceCorners = new[] { new[] { new ObjCorner(0), new ObjCorner(2), new ObjCorner(1) } } } }), "writer detects disagreement between face and corner topology");
        bool unsafeLibrary = false;
        try { ObjMeshFormat.Write(new(new[] { Mesh() }), "Tank.mtl\nf 99 99 99"); } catch (ArgumentException) { unsafeLibrary = true; }
        Check(unsafeLibrary, "material reference cannot inject geometry lines");
        string safeName = ObjMeshFormat.Write(new(new[] { Mesh("Tank\nf 99 99 99 # name") }));
        Check(ObjMeshFormat.Read(safeName).Meshes[0].Faces.Length == 1, "object names cannot inject geometry lines");
        Console.WriteLine($"OBJ format: {checks} checks passed");
    }
}
