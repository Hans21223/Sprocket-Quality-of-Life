using System.Numerics;
using System.Text.Json.Nodes;
using SprocketQoL;

/// Review DATA-02/03: parts made from a part keep its own paint; parts that go leave the paint lists.
static class OwnPaintEditTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Own paint edits: " + why); }
        static JsonObject Transform(float x) => JsonNode.Parse($$"""{"pos":[{{x}},0,0],"rot":[0,0,0,0],"scale":[1,1,1],"mirrorVuid":-1}""")!.AsObject();
        static JsonObject Part(int id, int parent, int block, float x = 0) => new()
        {
            ["vuid"] = id, ["pvuid"] = parent, ["guid"] = parent < 0 ? Conversion.CompartmentGuid : Conversion.AddonGuid, ["flags"] = 0,
            ["plateStructure"] = id + 100, ["structureBlueprintVuid"] = block, ["transform"] = Transform(x),
        };
        static JsonObject Fixture()
        {
            var points = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) };
            var faces = new List<int[]> { new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 } };
            var mesh = AddonEdits.MeshData(points, faces, 10, 0).ToJsonString();
            return new JsonObject
            {
                ["header"] = new JsonObject { ["name"] = "Own paint fixture" },
                ["objects"] = new JsonArray(Part(0, -1, 1), Part(10, 0, 2, 2), Part(20, 0, 3, -2)),
                ["blueprints"] = new JsonArray(
                    new JsonObject { ["id"] = 1, ["type"] = "structure", ["blueprint"] = new JsonObject { ["bodyMeshVuid"] = 1, ["armourVolume"] = .06 } },
                    new JsonObject { ["id"] = 2, ["type"] = "structure", ["blueprint"] = new JsonObject { ["bodyMeshVuid"] = 2, ["armourVolume"] = .06 } },
                    new JsonObject { ["id"] = 3, ["type"] = "structure", ["blueprint"] = new JsonObject { ["bodyMeshVuid"] = 3, ["armourVolume"] = .06 } },
                    JsonNode.Parse("""{"id":50,"type":"paintJob","blueprint":{"name":"Green","description":"Quality of Life parts: 10","r":0.25,"g":0.4,"b":0.15}}"""),
                    JsonNode.Parse("""{"id":52,"type":"paintJob","blueprint":{"name":"Red","description":"Quality of Life parts: 20","r":0.6,"g":0.1,"b":0.1}}"""),
                    JsonNode.Parse("""{"id":51,"type":"paintJobRegister","blueprint":{"paintJobIDs":[-1,-1,-1,-1,-1,50,52]}}""")),
                ["meshes"] = new JsonArray(
                    new JsonObject { ["vuid"] = 1, ["meshData"] = JsonNode.Parse(mesh) },
                    new JsonObject { ["vuid"] = 2, ["meshData"] = JsonNode.Parse(mesh) },
                    new JsonObject { ["vuid"] = 3, ["meshData"] = JsonNode.Parse(mesh) }),
            };
        }
        static int[] Owners(string json, int job) => Conversion.Parse(json)["blueprints"]!.AsArray().Single(b => b!["id"]!.GetValue<int>() == job)!["blueprint"]!["description"]!
            .GetValue<string>()["Quality of Life parts:".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        static int[] New(string json) => Conversion.Objects(Conversion.Parse(json)).Keys.Where(id => id is not (0 or 10 or 20)).ToArray();
        string original = Fixture().ToJsonString();

        var separate = AddonEdits.Separate(original, 10, new List<Vector3[]> { new[] { new Vector3(0, 0, 0), new Vector3(0, 1, 0), new Vector3(1, 1, 0), new Vector3(1, 0, 0) } });
        Check(separate.Parts.Count == 1 && Owners(separate.Json, 50).OrderBy(i => i).SequenceEqual(new[] { 10, separate.Parts[0].Added }),
            "a separated piece keeps the part's own paint, and the part keeps it too");
        Check(Owners(separate.Json, 52).SequenceEqual(new[] { 20 }), "another part's own paint is left alone");
        var scatter = AddonEdits.PlanScatter(original, 0, new[] { 10 }, 2, 42, true);
        Check(New(scatter.DesignJson).Length == 2 && New(scatter.DesignJson).All(Owners(scatter.DesignJson, 50).Contains), "scattered copies keep their type's own paint");
        var imaged = Fixture();
        imaged["objects"]![1]!["flags"] = 4;
        var mirror = AddonEdits.PlanRealMirror(imaged.ToJsonString(), 10);
        Check(New(mirror.DesignJson).Length == 1 && Owners(mirror.DesignJson, 50).Contains(New(mirror.DesignJson)[0]), "a mirror image made real keeps the part's own paint");
        var merge = AddonEdits.PlanMerge(original, 0, new[] { 10 });
        Check(!Conversion.Objects(Conversion.Parse(merge.DesignJson)).ContainsKey(10) && Owners(merge.DesignJson, 50).Length == 0, "a part merged away leaves its paint list");
        Check(Owners(merge.DesignJson, 52).SequenceEqual(new[] { 20 }), "a merge leaves other parts' own paint alone");
        Console.WriteLine($"OWN_PAINT_EDIT_TESTS_OK: {checks} checks");
    }
}
