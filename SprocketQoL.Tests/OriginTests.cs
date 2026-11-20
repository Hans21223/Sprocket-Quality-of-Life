using System.Numerics;
using System.Text.Json.Nodes;
using SprocketQoL;

static class OriginTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool condition, string reason) { checks++; if (!condition) throw new Exception("Origin: " + reason); }
        void Near(Vector3 actual, Vector3 expected, string reason) => Check(Vector3.Distance(actual, expected) < .0001f, reason);
        void Reject(Action action, string reason)
        {
            bool rejected = false;
            try { action(); } catch { rejected = true; }
            Check(rejected, reason);
        }
        var points = new[] { new Vector3(1, 2, 3), new Vector3(5, 2, 3), new Vector3(1, 8, 3), new Vector3(1, 2, 11), new Vector3(3, 4, 5) };
        JsonArray Vec(Vector3 p) => new(p.X, p.Y, p.Z);
        JsonObject Part(int id, int parent, Vector3 pos, Vector3 rot, Vector3 scale, int? structure = null, int twin = -1, int flags = 0)
        {
            var part = new JsonObject
            {
                ["guid"] = Conversion.AddonGuid, ["vuid"] = id, ["pvuid"] = parent, ["flags"] = flags,
                ["transform"] = new JsonObject { ["pos"] = Vec(pos), ["rot"] = new JsonArray(rot.X, rot.Y, rot.Z, 0), ["scale"] = Vec(scale), ["mirrorVuid"] = twin },
            };
            if (structure is int sid) part["structureBlueprintVuid"] = sid;
            return part;
        }
        JsonObject Fixture(bool mirrored = false, bool shared = false)
        {
            var objects = new JsonArray(
                Part(0, -1, mirrored ? Vector3.Zero : new(2, -1, 5), mirrored ? Vector3.Zero : new(7, 13, -11), Vector3.One),
                Part(1, 0, new(3, 1, 4), new(0, 30, 0), new(2, 3, 4), 10, mirrored ? 2 : -1),
                Part(3, 1, new(.3f, -.2f, .8f), new(15, 20, -25), new(.5f, .75f, 1.25f)),
                Part(6, 3, new(-.4f, .2f, .1f), new(-5, 12, 3), Vector3.One));
            if (mirrored)
            {
                objects.Add(Part(2, 0, new(-3, 1, 4), new(0, -30, 0), new(2, 3, 4), 10, 1, 1));
                objects.Add(Part(7, 2, new(-.3f, -.2f, .8f), Vector3.Zero, Vector3.One));
            }
            var blocks = new JsonArray(new JsonObject { ["id"] = 10, ["type"] = "structure", ["blueprint"] = new JsonObject { ["bodyMeshVuid"] = 20, ["armourVolume"] = 1.23, ["customSetting"] = "preserve" } });
            if (shared)
            {
                objects.Add(Part(4, 0, new(7, 0, -2), Vector3.Zero, Vector3.One, 10));
                objects.Add(Part(5, 0, new(9, 0, -2), Vector3.Zero, Vector3.One, 11));
                blocks.Add(new JsonObject { ["id"] = 11, ["type"] = "structure", ["blueprint"] = new JsonObject { ["bodyMeshVuid"] = 20, ["armourVolume"] = 2.34 } });
            }
            var data = AddonEdits.MeshData(points.ToList(), new() { new[] { 0, 1, 4 }, new[] { 1, 2, 4 }, new[] { 2, 3, 4 }, new[] { 3, 0, 4 }, new[] { 0, 3, 2, 1 } }, 12, 30);
            data["customMeshSetting"] = "preserve";
            return new JsonObject
            {
                ["header"] = new JsonObject { ["name"] = "Origin regression fixture" }, ["objects"] = objects, ["blueprints"] = blocks,
                ["meshes"] = new JsonArray(new JsonObject { ["vuid"] = 20, ["meshData"] = data }),
            };
        }
        JsonObject Data(string json, int id)
        {
            var b = Conversion.Parse(json);
            return AddonEdits.MeshOf(b["meshes"]!.AsArray(), AddonEdits.MeshIdOf(json, id))!;
        }
        Vector3[] Vertices(string json, int id)
        {
            var raw = Data(json, id)["mesh"]!["vertices"]!.AsArray();
            return Enumerable.Range(0, raw.Count / 3).Select(i => new Vector3(MeshCut.F(raw[3 * i]), MeshCut.F(raw[3 * i + 1]), MeshCut.F(raw[3 * i + 2]))).ToArray();
        }
        void PreservesWorld(string beforeJson, AddonEdits.EditPlan plan, string reason, int focus = 1)
        {
            var before = Conversion.Objects(Conversion.Parse(beforeJson)); var after = Conversion.Objects(Conversion.Parse(plan.DesignJson));
            var beforeWorld = Conversion.WorldMatrices(before); var afterWorld = Conversion.WorldMatrices(after);
            Check(before.Keys.OrderBy(x => x).SequenceEqual(after.Keys.OrderBy(x => x)), reason + ": keeps every part");
            foreach (int id in before.Keys)
            {
                if (!plan.MeshIds.ContainsKey(id)) Check(Conversion.Near(beforeWorld[id], afterWorld[id]), reason + $": part {id} world frame unchanged");
                if (before[id]["structureBlueprintVuid"] == null) continue;
                var oldPoints = Vertices(beforeJson, id); var newPoints = Vertices(plan.DesignJson, id);
                var oldShape = AddonEdits.Shape(before[id], beforeWorld[id]); var newShape = AddonEdits.Shape(after[id], afterWorld[id]);
                Check(oldPoints.Length == newPoints.Length, reason + ": keeps point count");
                for (int i = 0; i < oldPoints.Length; i++) Near(Vector3.Transform(newPoints[i], newShape), Vector3.Transform(oldPoints[i], oldShape), reason + $": part {id} point {i} stays in place");
            }
            var beforeData = Data(beforeJson, 1); var afterData = Data(plan.DesignJson, 1);
            beforeData["mesh"]!.AsObject().Remove("vertices"); afterData["mesh"]!.AsObject().Remove("vertices");
            Check(JsonNode.DeepEquals(beforeData, afterData), reason + ": topology, thickness, corner modes, flags, rivets and mesh settings unchanged");
            Check(!plan.Live && plan.Remove.Count == 0 && plan.Reparent.Count == 0 && plan.Focus == focus, reason + ": origin edit retains reload and backup path");
        }

        var fixture = Fixture(); string source = fixture.ToJsonString();
        var selected = new[] { 0, 1, 4, 4, 4 };
        var mean = new Vector3(3, 8f / 3, 11f / 3); var bounds = new Vector3(3, 3, 4);
        foreach (var (mode, centre) in new[] { (AddonEdits.OriginMode.SelectedPointsCenter, mean), (AddonEdits.OriginMode.SelectedPointsBounds, bounds) })
        {
            var plan = AddonEdits.PlanOrigin(source, 1, mode, selected, points);
            var shifted = Vertices(plan.DesignJson, 1);
            for (int i = 0; i < points.Length; i++) Near(shifted[i], points[i] - centre, $"{mode}: unique selected IDs determine the centre");
            var oldObjects = Conversion.Objects(Conversion.Parse(source)); var oldWorld = Conversion.WorldMatrices(oldObjects);
            var newWorld = Conversion.WorldMatrices(Conversion.Objects(Conversion.Parse(plan.DesignJson)));
            Near(newWorld[1].Translation, Vector3.Transform(centre, AddonEdits.Shape(oldObjects[1], oldWorld[1])), $"{mode}: origin reaches selected location under parent rotation and nonuniform part scale");
            PreservesWorld(source, plan, mode.ToString());
            Reject(() => AddonEdits.PlanOrigin(plan.DesignJson, 1, mode, selected), mode + ": already centred edit is refused");
        }
        Check(fixture.ToJsonString() == source, "planning never mutates the supplied fixture");
        var single = AddonEdits.PlanOrigin(source, 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 4 });
        Near(Vertices(single.DesignJson, 1)[4], Vector3.Zero, "single selected point becomes the origin");
        PreservesWorld(source, single, "single point");
        var legacy = AddonEdits.PlanOrigin(source, 1);
        var explicitWhole = AddonEdits.PlanOrigin(source, 1, AddonEdits.OriginMode.WholeShapeBounds);
        Check(legacy.DesignJson == explicitWhole.DesignJson, "legacy API keeps whole-shape bounds behavior");
        Near(Vertices(legacy.DesignJson, 1)[0], points[0] - new Vector3(3, 5, 7), "whole-shape mode includes unselected extrema");

        foreach (bool mirrored in new[] { false, true })
        {
            string shared = Fixture(mirrored, true).ToJsonString();
            var plan = AddonEdits.PlanOrigin(shared, 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0, 1, 4 }, points);
            PreservesWorld(shared, plan, mirrored ? "mirror pair and shared mesh" : "shared mesh");
            Check(plan.MeshIds.Keys.OrderBy(x => x).SequenceEqual(mirrored ? new[] { 1, 2 } : new[] { 1 }), "only target and true mirror twin receive new origins");
            Check(AddonEdits.MeshIdOf(plan.DesignJson, 1) != 20 && AddonEdits.MeshIdOf(plan.DesignJson, 4) == 20 && AddonEdits.MeshIdOf(plan.DesignJson, 5) == 20, "unrelated users keep the old shared mesh and structure settings");
            Check(JsonNode.DeepEquals(Data(shared, 4), Data(plan.DesignJson, 4)), "unrelated shared mesh stays byte-for-value identical");
            if (mirrored)
            {
                Check(AddonEdits.MeshIdOf(plan.DesignJson, 1) == AddonEdits.MeshIdOf(plan.DesignJson, 2), "mirror pair continues sharing its copied mesh");
                var objects = Conversion.Objects(Conversion.Parse(plan.DesignJson));
                Check(objects[1]["transform"]!["mirrorVuid"]!.GetValue<int>() == 2 && objects[2]["transform"]!["mirrorVuid"]!.GetValue<int>() == 1, "mirror links remain reciprocal");
                var flippedPlan = AddonEdits.PlanOrigin(shared, 2, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0, 1, 4 }, points);
                PreservesWorld(shared, flippedPlan, "selecting the flipped mirror twin", 2);
                var oldObjects = Conversion.Objects(Conversion.Parse(shared)); var oldWorld = Conversion.WorldMatrices(oldObjects);
                var newWorld = Conversion.WorldMatrices(Conversion.Objects(Conversion.Parse(flippedPlan.DesignJson)));
                Near(newWorld[2].Translation, Vector3.Transform(mean, AddonEdits.Shape(oldObjects[2], oldWorld[2])), "flipped twin origin follows its displayed selected location");
            }
        }

        foreach (var ids in new IReadOnlyCollection<int>?[] { null, Array.Empty<int>(), new[] { -1 }, new[] { points.Length }, new[] { 0, int.MaxValue } })
            foreach (var mode in new[] { AddonEdits.OriginMode.SelectedPointsCenter, AddonEdits.OriginMode.SelectedPointsBounds })
                Reject(() => AddonEdits.PlanOrigin(source, 1, mode, ids), mode + ": invalid selection is refused without whole-shape fallback");
        Reject(() => AddonEdits.PlanOrigin(source, 1, (AddonEdits.OriginMode)999, new[] { 0 }), "unknown mode refused");
        Reject(() => AddonEdits.PlanOrigin(source, 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0 }, points[..^1]), "changed snapshot point count refused");
        var reordered = points.ToArray(); (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        Reject(() => AddonEdits.PlanOrigin(source, 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0 }, reordered), "reordered snapshot refused");
        var moved = points.ToArray(); moved[3] += new Vector3(.001f, 0, 0);
        Reject(() => AddonEdits.PlanOrigin(source, 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0 }, moved), "changed unselected snapshot point refused");
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var invalid = points.ToArray(); invalid[2] = new Vector3(bad, 2, 3);
            Reject(() => AddonEdits.PlanOrigin(source, 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0 }, invalid), "non-finite snapshot refused");
        }
        foreach (JsonNode? bad in new JsonNode?[] { null, JsonValue.Create("NaN"), JsonValue.Create("1"), JsonValue.Create(true), JsonValue.Create(1e100) })
        {
            var invalid = Fixture(); invalid["meshes"]![0]!["meshData"]!["mesh"]!["vertices"]![12] = bad;
            Reject(() => AddonEdits.PlanOrigin(invalid.ToJsonString(), 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0 }), "missing, string, nonnumeric or non-finite coordinates refused even when unselected");
        }
        var malformed = Fixture(); malformed["meshes"]![0]!["meshData"]!["mesh"]!["vertices"]!.AsArray().RemoveAt(0);
        Reject(() => AddonEdits.PlanOrigin(malformed.ToJsonString(), 1, AddonEdits.OriginMode.SelectedPointsBounds, new[] { 0 }), "incomplete coordinate triplet refused");
        var extreme = Fixture(); extreme["meshes"]![0]!["meshData"]!["mesh"]!["vertices"]![0] = -float.MaxValue; extreme["meshes"]![0]!["meshData"]!["mesh"]!["vertices"]![3] = float.MaxValue;
        Reject(() => AddonEdits.PlanOrigin(extreme.ToJsonString(), 1, AddonEdits.OriginMode.SelectedPointsCenter, new[] { 0 }), "origin offset that would overflow another point refused");
        Console.WriteLine($"ORIGIN_TESTS_OK {checks}");
    }
}
