using System.Text.Json.Nodes;
using SprocketQoL;

static class PartPaintPersistenceTests
{
    const int First = 5, Last = 13; // current native User1 / User9
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Part paint persistence: " + why); }

    // Same field names and versions as the native saves surveyed; IDs and part
    // names are synthetic, and every test stays in memory.
    static JsonObject NativeFixture() => JsonNode.Parse("""
        {
          "v":"2.0",
          "header":{"name":"Paint fixture"},
          "objects":[{"vuid":0,"pvuid":-1},{"vuid":10,"pvuid":0},{"vuid":11,"pvuid":0},{"vuid":12,"pvuid":0}],
          "blueprints":[
            {"id":1,"type":"paintJob","blueprint":{"v":"2.2","name":"Primary","description":"","colourMapUrl":"","scale":0.5,"roughness":0.5,"metallic":0.0,"r":0.75,"g":0.75,"b":0.75,"saturation":0.5,"condition":0.4,"grime":0.0}},
            {"id":2,"type":"paintJob","blueprint":{"v":"2.2","name":"Powertrain","description":"","colourMapUrl":"game-camo","scale":0.5,"roughness":0.5,"metallic":0.0,"r":0.25,"g":0.4,"b":0.5,"saturation":0.75,"condition":0.75,"grime":0.0}},
            {"id":3,"type":"paintJob","blueprint":{"v":"2.2","name":"Interior","description":"","colourMapUrl":"","scale":0.5,"roughness":0.6,"metallic":0.0,"r":0.5,"g":0.5,"b":0.5,"saturation":0.5,"condition":-1.0,"grime":0.0}},
            {"id":20,"type":"paintJob","blueprint":{"v":"2.2","name":"Preset 1","description":"Quality of Life parts: 10 11","colourMapUrl":"Sprocket/Paint/fixture.png","scale":1.25,"roughness":0.3,"metallic":0.15,"r":0.2,"g":0.45,"b":0.65,"saturation":0.75,"condition":0.6,"grime":0.1}},
            {"id":21,"type":"paintJob","blueprint":{"v":"2.0","name":"Preset 2","description":"Quality of Life parts: 12","colourMapUrl":"","scale":0.75,"roughness":0.8,"metallic":0.0,"r":0.8,"g":0.15,"b":0.1,"saturation":0.4,"condition":0.9,"grime":0.2}},
            {"id":30,"type":"paintJobRegister","blueprint":{"v":"0.0","name":null,"description":null,"paintJobIDs":[1,2,3,-1,-1,20,21]}}
          ]
        }
        """)!.AsObject();

    static JsonObject[] Defaults() => Enumerable.Range(0, 3)
        .Select(i => new JsonObject { ["name"] = i == 0 ? "Primary fallback" : "Native default " + i }).ToArray();

    public static void Run()
    {
        checks = 0;
        var defaults = Defaults();
        string[] original = defaults.Select(job => job.ToJsonString()).ToArray();
        var normal = PartPaintPersistence.NormalizeDefaults(defaults, First + 2, First, Last, true)!;
        Check(normal.Length == First + 2, "saved user slots extend the native constructor's load bound");
        Check(!ReferenceEquals(defaults, normal), "extension allocates its own array");
        for (int i = 0; i < defaults.Length; i++)
            Check(ReferenceEquals(normal[i], defaults[i]), "existing default slot is retained " + i);
        for (int i = defaults.Length; i < normal.Length; i++)
            Check(ReferenceEquals(normal[i], defaults[0]), "new slots have a valid primary fallback " + i);
        Check(defaults.Select(job => job.ToJsonString()).SequenceEqual(original), "default jobs are never modified");
        normal[0] = new JsonObject { ["name"] = "replacement" };
        Check(defaults[0].ToJsonString() == original[0], "replacing an output element cannot alter the input array");

        foreach (int count in new[] { int.MinValue, -1, 0, 1, First, Last + 2, int.MaxValue })
            Check(ReferenceEquals(PartPaintPersistence.NormalizeDefaults(defaults, count, First, Last, true), defaults), "invalid/non-extending saved count leaves defaults alone " + count);
        foreach (int count in Enumerable.Range(First + 1, Last - First + 1))
            Check(PartPaintPersistence.NormalizeDefaults(defaults, count, First, Last, true)!.Length == count, "each supported user slot loads " + count);
        Check(ReferenceEquals(PartPaintPersistence.NormalizeDefaults(defaults, First + 2, First, Last, false), defaults), "unrelated paint jobs never change constructor defaults");
        foreach (var (first, last) in new[] { (-1, Last), (First, -1), (Last, First), (First, int.MaxValue) })
            Check(ReferenceEquals(PartPaintPersistence.NormalizeDefaults(defaults, First + 2, first, last, true), defaults), "malformed slot bounds safely decline");
        var futureDefaults = Enumerable.Range(0, Last + 4).Select(i => new JsonObject()).ToArray();
        Check(ReferenceEquals(PartPaintPersistence.NormalizeDefaults(futureDefaults, Last + 1, First, Last, true), futureDefaults), "future larger native defaults are not shortened or replaced");
        Check(PartPaintPersistence.NormalizeDefaults<JsonObject>(null, First + 2, First, Last, true) == null, "null defaults safely decline");
        var empty = Array.Empty<JsonObject>();
        Check(ReferenceEquals(PartPaintPersistence.NormalizeDefaults(empty, First + 2, First, Last, true), empty), "empty defaults safely decline");
        var allNull = new JsonObject[First];
        Check(ReferenceEquals(PartPaintPersistence.NormalizeDefaults(allNull, First + 2, First, Last, true), allNull), "all-null defaults safely decline");
        var firstNull = Defaults(); firstNull[0] = null!;
        var fallback = PartPaintPersistence.NormalizeDefaults(firstNull, First + 2, First, Last, true)!;
        Check(fallback[0] == null && ReferenceEquals(fallback[First], firstNull[1]), "first nonnull default is a safe fallback without filling existing nulls");

        foreach (bool useLastSlot in new[] { false, true })
        {
            var saved = NativeFixture();
            var blocks = saved["blueprints"]!.AsArray();
            var register = blocks.Single(block => block!["type"]!.GetValue<string>() == "paintJobRegister")!["blueprint"]!.AsObject();
            var ids = register["paintJobIDs"]!.AsArray();
            if (useLastSlot)
            {
                ids.RemoveAt(First + 1);
                while (ids.Count < Last) ids.Add(-1);
                ids.Add(21);
            }
            string fixtureJson = saved.ToJsonString();
            var reloaded = JsonNode.Parse(fixtureJson)!.AsObject();
            var loadedBlocks = reloaded["blueprints"]!.AsArray();
            var loadedRegister = loadedBlocks.Single(block => block!["type"]!.GetValue<string>() == "paintJobRegister")!["blueprint"]!.AsObject();
            int[] savedIds = loadedRegister["paintJobIDs"]!.AsArray().Select(id => id!.GetValue<int>()).ToArray();
            // The native saved-load loop is bounded by its defaults array. Verify
            // that tagged saved slots are inside that bound instead of truncated.
            Check(savedIds.Skip(defaults.Length).Contains(20), "old native load bound excludes saved Preset 1");
            var loadDefaults = PartPaintPersistence.NormalizeDefaults(defaults, savedIds.Length, First, Last, true)!;
            var loadedIds = savedIds.Take(loadDefaults.Length).ToArray();
            Check(loadedIds.SequenceEqual(savedIds), "normalized native load bound covers every saved slot");
            Check(loadedIds[First] == 20 && loadedIds[useLastSlot ? Last : First + 1] == 21, "preset IDs stay in the saved material slots");
            foreach (int id in new[] { 20, 21 })
            {
                var before = blocks.Single(block => block!["id"]!.GetValue<int>() == id)!["blueprint"]!.AsObject();
                var after = loadedBlocks.Single(block => block!["id"]!.GetValue<int>() == id)!["blueprint"]!.AsObject();
                Check(before.ToJsonString() == after.ToJsonString(), "preset fields, camouflage and ownership description survive reparse " + id);
                Check(after["description"]!.GetValue<string>().StartsWith("Quality of Life parts:", StringComparison.Ordinal), "owner record stays on the saved paint job " + id);
            }
            Check(!loadedRegister.ContainsKey("nativeSlotPaintJobIds"), "native JSON uses paintJobIDs rather than a runtime field name");
            Check(fixtureJson == saved.ToJsonString(), "normalization never rewrites the saved JSON fixture");
            Check(reloaded["objects"]!.ToJsonString() == saved["objects"]!.ToJsonString(), "paint persistence preserves owner part IDs and hierarchy");
        }
        Console.WriteLine($"PART_PAINT_PERSISTENCE_TESTS_OK: {checks} checks (native slot bounds, saved metadata, owner IDs, malformed input)");
    }
}
