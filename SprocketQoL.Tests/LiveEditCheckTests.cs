using System.Text.Json.Nodes;
using SprocketQoL;

/// Review CORE-02: an in-place edit's trial compares each part's whole shape, not only its face count.
static class LiveEditCheckTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception("Live edit check: " + reason); }
        static string Design(JsonObject meshData) => new JsonObject
        {
            ["objects"] = new JsonArray(new JsonObject { ["guid"] = Conversion.AddonGuid, ["vuid"] = 2, ["pvuid"] = -1, ["flags"] = 2, ["structureBlueprintVuid"] = 11 }),
            ["blueprints"] = new JsonArray(new JsonObject { ["id"] = 11, ["type"] = "structure", ["blueprint"] = new JsonObject { ["bodyMeshVuid"] = 111 } }),
            ["meshes"] = new JsonArray(new JsonObject { ["vuid"] = 111, ["meshData"] = meshData }),
        }.ToJsonString();
        static string Key(Action<JsonObject> change)
        {
            var md = AddonEdits.Cylinder(0.2f, 0.3f, 6, 10).MeshData;
            change(md);
            return AddonEdits.ShapeKeys(Design(md))[2];
        }
        static JsonArray Faces(JsonObject md) => md["mesh"]!["faces"]!.AsArray();
        string same = Key(_ => { });

        Check(Key(_ => { }) == same, "one shape gives one key");
        Check(Key(md => { var list = Faces(md).Select(f => f!.DeepClone()).Reverse().ToList(); Faces(md).Clear(); list.ForEach(Faces(md).Add); }) == same,
            "the order the faces are listed in doesn't matter");
        Check(Key(md => { var v = Faces(md)[0]!["v"]!.AsArray(); var c = v.Select(x => x!.GetValue<int>()).ToArray(); for (int k = 0; k < c.Length; k++) v[k] = c[(k + 1) % c.Length]; }) == same,
            "which corner a face is listed from doesn't matter");
        Check(Key(md => Faces(md)[0]!["t"]![0] = 10.0) == same, "10 and 10.0 mm are one thickness");
        Check(Key(md => { var v = Faces(md)[0]!["v"]!.AsArray(); var c = v.Select(x => x!.GetValue<int>()).Reverse().ToArray(); for (int k = 0; k < c.Length; k++) v[k] = c[k]; }) != same,
            "a face turned over is a different shape");
        Check(Key(md => { var xs = md["mesh"]!["vertices"]!.AsArray(); xs[0] = xs[0]!.GetValue<float>() + 0.001f; }) != same, "a point moved 1 mm counts");
        Check(Key(md => Faces(md)[0]!["t"]![0] = 11) != same, "a changed thickness counts, with the face count unchanged");
        Check(Key(md => Faces(md)[0]!["tm"] = 0x04010101) != same, "a changed corner mode counts");
        Check(Key(md => md["mesh"]!["edgeFlags"]![0] = 1) != same, "a weld mark counts");
        Check(Key(md => md["rivets"]!["nodes"]!.AsArray().Add(new JsonObject { ["face"] = 0 })) != same, "a rivet more or less counts");
        Check(Key(md => md["smoothAngle"] = 10) != same, "the smoothing angle counts");
        static string Faces2(Action<JsonObject> change) { var md = AddonEdits.Cylinder(0.2f, 0.3f, 6, 10).MeshData; change(md); return AddonEdits.ShapeKeys(Design(md), full: false)[2]; }
        string where = Faces2(_ => { });
        Check(Faces2(md => Faces(md)[0]!["tm"] = 0x04010101) == where && Faces2(md => Faces(md)[0]!["t"]![0] = 11) == where,
            "the faces-only key leaves out corner settings the game may tidy on loading");
        Check(Faces2(md => { var xs = md["mesh"]!["vertices"]!.AsArray(); xs[0] = xs[0]!.GetValue<float>() + 0.001f; }) != where, "the faces-only key still sees a moved point");
        Check(AddonEdits.FaceCounts(Design(AddonEdits.Cylinder(0.2f, 0.3f, 6, 10).MeshData))[2] == 18, "face counts read through the same parts");
        Console.WriteLine($"LIVE_EDIT_CHECK_TESTS_OK: {checks} checks");
    }
}
