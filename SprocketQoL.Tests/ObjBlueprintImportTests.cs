using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SprocketQoL;

static class ObjBlueprintImportTests
{
    static int checks;
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception("OBJ plate-structure import: " + message); }
    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
    static Vector3[] Points(JsonNode data)
    {
        var coordinates = data["mesh"]!["vertices"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray();
        return Enumerable.Range(0, coordinates.Length / 3).Select(i => new Vector3(coordinates[i * 3], coordinates[i * 3 + 1], coordinates[i * 3 + 2])).ToArray();
    }
    static int[][] Faces(JsonNode data) => data["mesh"]!["faces"]!.AsArray().Select(f => f!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray()).ToArray();
    static void MustReject(Action action, string reason)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (FormatException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, reason);
    }
    static ObjDocument TwoObjects() => new(new[]
    {
        new ObjMeshData("Main body", new[] { new Vector3(-2, 0, -3), new Vector3(2, 0, -3), new Vector3(2, 0, 3), new Vector3(-2, 0, 3) }, new[] { new[] { 0, 1, 2, 3 } }),
        new ObjMeshData("Separated turret", new[] { new Vector3(7, 2, 5), new Vector3(9, 2, 5), new Vector3(9, 3, 5) }, new[] { new[] { 0, 1, 2 } }),
    });
    static void NativeSchema(JsonObject blueprint, float thickness)
    {
        Check(blueprint["v"]!.GetValue<string>() == "0.2" && blueprint["format"]!.GetValue<string>() == "freeform", "saved native plate format and version");
        Check(blueprint["gridSize"]!.GetValue<int>() == 1 && blueprint["smoothAngle"]!.GetValue<int>() == 0, "editing grid and hard-edge defaults");
        Check(blueprint["header"] == null && blueprint["objects"] == null && blueprint["blueprints"] == null && blueprint["vuid"] == null && blueprint["meshData"] == null, "bare saved plate blueprint, without vehicle or embedded mesh wrapper");
        var mesh = blueprint["mesh"]!.AsObject();
        Check(mesh["majorVersion"]!.GetValue<int>() == 0 && mesh["minorVersion"]!.GetValue<int>() == 3, "native mesh version");
        var points = Points(blueprint); var faces = Faces(blueprint);
        var edgeArray = mesh["edges"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
        var edgeSet = Enumerable.Range(0, edgeArray.Length / 2).Select(i => (Math.Min(edgeArray[i * 2], edgeArray[i * 2 + 1]), Math.Max(edgeArray[i * 2], edgeArray[i * 2 + 1]))).ToHashSet();
        Check(edgeArray.Length % 2 == 0 && edgeSet.Count * 2 == edgeArray.Length, "unique indexed edge pairs");
        Check(edgeArray.All(index => index >= 0 && index < points.Length), "edge references resolve");
        Check(mesh["edgeFlags"]!.AsArray().Count == edgeArray.Length / 2 && mesh["edgeFlags"]!.AsArray().All(x => x!.GetValue<int>() == 0), "all imported edges unlocked");
        foreach (var faceNode in mesh["faces"]!.AsArray())
        {
            var face = faceNode!["v"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            Check(face.Length is 3 or 4 && face.All(index => index >= 0 && index < points.Length), "native face indices resolve with supported corner count");
            Check(faceNode["t"]!.AsArray().Count == face.Length && faceNode["t"]!.AsArray().All(x => x!.GetValue<int>() == thickness), "whole-mm plate thickness per corner");
            Check(faceNode["tm"]!.GetValue<int>() == (face.Length == 4 ? 0x01010101 : 0x010101), "native packed automatic thickness modes");
            using var nativeNumbers = JsonDocument.Parse(faceNode.ToJsonString());
            Check(nativeNumbers.RootElement.GetProperty("te").GetUInt64() == 0, "unsigned thicken-edge JSON value loads without overflow");
            for (int i = 0; i < face.Length; i++)
                Check(edgeSet.Contains((Math.Min(face[i], face[(i + 1) % face.Length]), Math.Max(face[i], face[(i + 1) % face.Length]))), "all polygon boundary edges exist");
        }
        Check(blueprint["rivets"]!["nodes"]!.AsArray().Count == 0 && blueprint["rivets"]!["profiles"]!.AsArray().Count == 1, "native rivet map initialized without stale references");
    }
    static void PositionChecks(JsonObject blueprint, ObjDocument source, float scale)
    {
        var points = Points(blueprint); var faces = Faces(blueprint);
        int vertexOffset = 0, faceOffset = 0;
        for (int m = 0; m < source.Meshes.Length; m++)
        {
            var objectMesh = source.Meshes[m];
            foreach (var vertex in objectMesh.Vertices)
            {
                var expected = new Vector3(-vertex.X, vertex.Y, vertex.Z) * scale;
                Check(Vector3.Distance(points[vertexOffset++], expected) < 1e-6f, "all objects retain common origin, Y-up axes, metres and relative placement");
            }
            int offset = vertexOffset - objectMesh.Vertices.Length;
            foreach (var face in objectMesh.Faces)
                Check(faces[faceOffset++].SequenceEqual(face.Reverse().Select(index => index + offset)), "face winding reversed once and indices shifted only by object vertex offset");
        }
    }
    public static void Run()
    {
        checks = 0;
        var model = TwoObjects();
        string unchanged = ObjMeshFormat.Write(model);
        var result = ObjBlueprintImport.Build(model, "Imported tank");
        var blueprint = Parse(result.Json);
        Check(result.ObjectCount == 2 && result.VertexCount == 7 && result.FaceCount == 2, "one plate structure contains all source objects and reports geometry counts");
        Check(blueprint["name"]!.GetValue<string>() == "Imported tank", "user's saved plate name");
        NativeSchema(blueprint, 10);
        PositionChecks(blueprint, model, 1);
        var scaled = ObjBlueprintImport.Build(model, "  Millimetres  ", .001f, 17);
        var scaledBlueprint = Parse(scaled.Json);
        Check(scaledBlueprint["name"]!.GetValue<string>() == "Millimetres", "name trimmed for native selector");
        NativeSchema(scaledBlueprint, 17);
        PositionChecks(scaledBlueprint, model, .001f);
        Check(Parse(ObjBlueprintImport.Build(model, "  ").Json)["name"]!.GetValue<string>() == "Imported OBJ", "empty display name has a usable default");
        var quoted = "Test \"plate\" \u03a9";
        Check(Parse(ObjBlueprintImport.Build(model, quoted).Json)["name"]!.GetValue<string>() == quoted, "JSON preserves quotes and Unicode names");

        // Concave polygons must preserve their exact covered area; fan triangulation would fill outside it.
        var concavePoints = new[] { Vector3.Zero, new Vector3(2, 0, 0), new Vector3(2, 2, 0), new Vector3(1, 1, 0), new Vector3(0, 2, 0) };
        var concaveModel = new ObjDocument(new[] { new ObjMeshData("Concave", concavePoints, new[] { new[] { 0, 1, 2, 3, 4 } }) });
        var concave = Parse(ObjBlueprintImport.Build(concaveModel, "Concave").Json);
        var cp = Points(concave); var cf = Faces(concave);
        double area = cf.Sum(f => Vector3.Cross(cp[f[1]] - cp[f[0]], cp[f[2]] - cp[f[0]]).Length() / 2.0);
        Check(cf.Length == 3 && cf.All(f => f.Length == 3) && Math.Abs(area - 3) < 1e-6, "concave boundary triangulated without overlapping or exterior triangles");
        NativeSchema(concave, 10);
        var bentModel = new ObjDocument(new[] { new ObjMeshData("Bent", new[] { Vector3.Zero, Vector3.UnitX, new Vector3(1, 1, .2f), Vector3.UnitY }, new[] { new[] { 0, 1, 2, 3 } }) });
        var bent = Parse(ObjBlueprintImport.Build(bentModel, "Bent").Json);
        Check(Faces(bent).Length == 2 && Faces(bent).All(face => face.Length == 3), "nonplanar quad becomes two editable triangles");

        // Hard normals and UV seams produce render duplicates. Weld inside each object, never across objects.
        var cube = Enumerable.Range(0, 8).Select(i => new Vector3(i & 1, (i >> 1) & 1, (i >> 2) & 1)).ToArray();
        var cubeFaces = new[] { new[] { 0, 4, 5, 1 }, new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 }, new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 } };
        var renderPoints = cubeFaces.SelectMany(f => f.Select(i => cube[i])).ToArray();
        var renderFaces = Enumerable.Range(0, 6).Select(i => Enumerable.Range(i * 4, 4).ToArray()).ToArray();
        var splitCube = new ObjDocument(new[] { new ObjMeshData("UV seams", renderPoints, renderFaces), new ObjMeshData("Coincident separate object", renderPoints, renderFaces) });
        var weldedResult = ObjBlueprintImport.Build(splitCube, "Separate shells");
        var welded = Parse(weldedResult.Json);
        Check(weldedResult.ObjectCount == 2 && weldedResult.VertexCount == 16 && weldedResult.FaceCount == 12, "two coincident objects stay disconnected while each shell is welded");
        Check(welded["mesh"]!["edges"]!.AsArray().Count == 48, "two shells retain twenty-four separate edges");
        var polygons = Faces(welded);
        Check(polygons.Take(6).All(face => face.All(index => index < 8)) && polygons.Skip(6).All(face => face.All(index => index >= 8)), "no topology connects distinct OBJ objects");
        for (int m = 0; m < 2; m++)
        {
            var shellEdges = polygons.Skip(m * 6).Take(6).SelectMany(f => f.Select((v, i) => (A: v, B: f[(i + 1) % f.Length]))).GroupBy(e => (Math.Min(e.A, e.B), Math.Max(e.A, e.B)));
            Check(shellEdges.All(e => e.Count() == 2 && e.First().A == e.Last().B), "welded cube remains a consistent closed shell");
        }
        NativeSchema(welded, 10);
        Check(renderPoints.Length == 24 && renderFaces[1].SequenceEqual(new[] { 4, 5, 6, 7 }), "welding leaves caller renderer arrays untouched");
        var seam = new ObjDocument(new[] { new ObjMeshData("Near seam", new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, new Vector3(.0000005f, 0, 0), Vector3.UnitX, Vector3.UnitZ }, new[] { new[] { 0, 1, 2 }, new[] { 3, 4, 5 } }) });
        Check(ObjBlueprintImport.Build(seam, "Near seam").VertexCount == 4, "default tolerance joins a sub-micrometre renderer seam");
        Check(ObjBlueprintImport.Build(seam, "Near seam", weldTolerance: 0).VertexCount == 5, "zero tolerance keeps intended small gaps and joins exact copies only");

        foreach (float scale in new[] { 0, -1, float.NaN, float.PositiveInfinity, 1001 }) MustReject(() => ObjBlueprintImport.Build(model, "Invalid", scale), "invalid scale rejected before any save");
        foreach (float thickness in new[] { 0, -1, float.NaN, float.PositiveInfinity, 1001, 10.5f }) MustReject(() => ObjBlueprintImport.Build(model, "Invalid", armourMm: thickness), "invalid or fractional thickness rejected before any save");
        foreach (float tolerance in new[] { -1, float.NaN, float.PositiveInfinity, 1, float.Epsilon }) MustReject(() => ObjBlueprintImport.Build(model, "Invalid", weldTolerance: tolerance), "invalid welding tolerance rejected");
        foreach (var mesh in new[]
        {
            new ObjMeshData("Empty", Array.Empty<Vector3>(), Array.Empty<int[]>()),
            new ObjMeshData("Bad index", new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, new[] { new[] { 0, 1, 3 } }),
            new ObjMeshData("Nonfinite", new[] { Vector3.Zero, Vector3.UnitX, new Vector3(float.NaN, 1, 0) }, new[] { new[] { 0, 1, 2 } }),
        }) MustReject(() => ObjBlueprintImport.Build(new ObjDocument(new[] { model.Meshes[0], mesh }), "Invalid"), "malformed second object rejects the entire planned plate blueprint");
        // Faces that draw nothing (real models have them: the game's own export did) are left out and counted, and an
        // object left with no faces is dropped; the rest still imports.
        foreach (var mesh in new[]
        {
            new ObjMeshData("Repeat", new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, new[] { new[] { 0, 1, 1 } }),
            new ObjMeshData("Degenerate", new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2 }, new[] { new[] { 0, 1, 2 } }),
            new ObjMeshData("Bowtie", new[] { Vector3.Zero, new Vector3(2, 2, 0), Vector3.UnitY, Vector3.UnitX }, new[] { new[] { 0, 1, 2, 3 } }),
            new ObjMeshData("Touching polygon", new[] { Vector3.Zero, new Vector3(3, 0, 0), new Vector3(3, 3, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(0, 3, 0) }, new[] { new[] { 0, 1, 2, 3, 4, 5 } }),
        })
        {
            var kept = ObjBlueprintImport.Build(new ObjDocument(new[] { model.Meshes[0], mesh }), "Skips");
            Check(kept.ObjectCount == 1 && kept.SkippedFaces == 1 && kept.VertexCount == 4 && kept.FaceCount == 1, $"{mesh.Name}: unusable face left out, the rest imported");
            NativeSchema(Parse(kept.Json), 10);
        }
        var sliver = new ObjMeshData("Sliver beside a face", new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitX * 2 }, new[] { new[] { 0, 1, 2 }, new[] { 0, 1, 3 } });
        var trimmed = ObjBlueprintImport.Build(new ObjDocument(new[] { sliver }), "Sliver");
        Check(trimmed.SkippedFaces == 1 && trimmed.FaceCount == 1 && trimmed.VertexCount == 3, "a sliver's unused vertex is dropped with it");
        MustReject(() => ObjBlueprintImport.Build(new ObjDocument(new[] { new ObjMeshData("All slivers", new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2 }, new[] { new[] { 0, 1, 2 } }) }), "Invalid"),
            "an OBJ with no usable face at all rejected");
        MustReject(() => ObjBlueprintImport.Build(new ObjDocument(Array.Empty<ObjMeshData>()), "Invalid"), "empty OBJ rejected");
        MustReject(() => ObjBlueprintImport.Build(null!, "Invalid"), "null OBJ rejected");
        var oversized = new ObjDocument(new[] { new ObjMeshData("Huge", new[] { new Vector3(2000, 0, 0), new Vector3(2001, 0, 0), new Vector3(2000, 1, 0) }, new[] { new[] { 0, 1, 2 } }) });
        MustReject(() => ObjBlueprintImport.Build(oversized, "Too large", 1000), "scaled coordinates beyond native supported range rejected");
        Check(unchanged == ObjMeshFormat.Write(model), "successful and failed conversions never mutate supplied OBJ data");

        // Compare the exact saved-file schema with a real native Plate Structures file, read-only.
        string sample = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Factions", "Default", "Blueprints", "Plate Structures", "Skirt.blueprint");
        if (File.Exists(sample))
        {
            var before = File.ReadAllBytes(sample);
            var native = Parse(File.ReadAllText(sample));
            Check(native.Select(property => property.Key).Order().SequenceEqual(blueprint.Select(property => property.Key).Order()), "top-level native saved plate schema matches an actual game-authored file");
            Check(native["mesh"]!.AsObject().Select(property => property.Key).Order().SequenceEqual(blueprint["mesh"]!.AsObject().Select(property => property.Key).Order()), "native mesh field schema matches an actual saved structure");
            Check(native["rivets"]!.AsObject().Select(property => property.Key).Order().SequenceEqual(blueprint["rivets"]!.AsObject().Select(property => property.Key).Order()), "native rivet schema matches actual saved structure");
            Check(before.SequenceEqual(File.ReadAllBytes(sample)), "sample user blueprint remains byte-identical after read-only validation");
        }
        Console.WriteLine($"OBJ plate-structure import: {checks} checks passed");
    }
}
