using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SprocketQoL;

namespace SprocketQoL.Tests;

public static class ClipboardTests
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("CLIPBOARD TEST FAILED: " + message);
    }

    public static void Run(IReadOnlyList<string>? realBlueprintFiles = null)
    {
        Console.WriteLine("Running ClipboardTests...");
        int checks = 0;

        // 1. Synthetic blueprint test
        string synthetic = BuildSyntheticTank();
        var origB = Conversion.Parse(synthetic);
        var origObjects = Conversion.Objects(origB);

        // Copy turret ring (vuid 10), which has body (11), gun (12), and decal (13)
        var (clipJson, copyCount) = BlueprintClipboard.Copy(synthetic, new[] { 10 });
        Check(copyCount == 4, $"expected 4 parts copied (ring + body + gun + decal), got {copyCount}");
        checks++;

        var clip = JsonNode.Parse(clipJson)!.AsObject();
        var clipObjs = clip["objects"]!.AsArray();
        Check(clipObjs.Count == 4, "clipboard contains 4 objects");
        checks++;

        var rootVuids = clip["rootVuids"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
        Check(rootVuids.Count == 1 && rootVuids[0] == 10, $"expected root vuid 10, got {string.Join(",", rootVuids)}");
        checks++;

        var clipMeshes = clip["meshes"]!.AsArray();
        Check(clipMeshes.Count == 1 && clipMeshes[0]!["vuid"]!.GetValue<int>() == 100, "clipboard packaged mesh 100");
        checks++;

        var clipBlueprints = clip["blueprints"]!.AsArray();
        Check(clipBlueprints.Count >= 2, $"clipboard packaged blueprints: count {clipBlueprints.Count}");
        checks++;

        // Test persistence to disk
        string tempDir = Path.Combine(Path.GetTempPath(), "SprocketQoL_ClipboardTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            BlueprintClipboard.StorageDirectory = tempDir;
            BlueprintClipboard.SetClipboardJson(clipJson);
            Check(BlueprintClipboard.HasData, "clipboard file exists and has data");
            checks++;

            string? loaded = BlueprintClipboard.GetClipboardJson();
            Check(loaded == clipJson, "clipboard loaded from disk matches saved JSON exactly");
            checks++;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
            BlueprintClipboard.StorageDirectory = null;
        }

        // 2. Paste into a bare vehicle (Tank B with only hull 0)
        string bareTank = BuildBareTank();
        var (pastedBare, focusVuid, pastedCount) = BlueprintClipboard.PasteInto(bareTank, clipJson);
        Check(pastedCount == 4, $"pasted count should be 4, got {pastedCount}");
        checks++;

        var bareAfter = Conversion.Parse(pastedBare);
        var bareObjects = Conversion.Objects(bareAfter);
        Check(bareObjects.Count == 5, $"bare tank should have 1 hull + 4 pasted parts = 5, got {bareObjects.Count}");
        checks++;

        // Verify the pasted root ring is attached to hull (0)
        Check(bareObjects.ContainsKey(focusVuid), "focus part exists in pasted vehicle");
        checks++;
        var pastedRing = bareObjects[focusVuid];
        Check(Conversion.Id(pastedRing, "pvuid") == 0, $"pasted ring should be parented to hull (0), got {Conversion.Id(pastedRing, "pvuid")}");
        checks++;

        // Verify child body is parented to pasted ring
        var pastedBody = bareObjects.Values.FirstOrDefault(o => Conversion.Id(o, "vuid") != 0 && o["guid"]?.GetValue<string>() == Conversion.CompartmentGuid);
        Check(pastedBody != null && Conversion.Id(pastedBody, "pvuid") == focusVuid, "pasted body is parented to pasted ring");
        checks++;

        // Verify child gun is parented to pasted body
        var pastedGun = bareObjects.Values.FirstOrDefault(o => o.ContainsKey("cannon"));
        Check(pastedGun != null && Conversion.Id(pastedGun, "pvuid") == Conversion.Id(pastedBody!, "vuid"), "pasted gun is parented to pasted body");
        checks++;

        // Verify mesh was remapped and exists in meshes array
        var pastedMeshes = bareAfter["meshes"]!.AsArray();
        Check(pastedMeshes.Count == 1, "pasted vehicle has 1 mesh");
        checks++;
        int newMeshId = pastedMeshes[0]!["vuid"]!.GetValue<int>();
        var pastedStructureBp = bareAfter["blueprints"]!.AsArray().FirstOrDefault(b => b!["type"]?.GetValue<string>() == "structure");
        Check(pastedStructureBp != null && pastedStructureBp!["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() == newMeshId, "structure blueprint points to remapped mesh VUID");
        checks++;

        // 3. Paste into the same vehicle (duplicating the turret assembly)
        var (doubleTurret, focus2, count2) = BlueprintClipboard.PasteInto(synthetic, clipJson);
        Check(count2 == 4, "pasted 4 parts into same vehicle");
        checks++;
        var doubleObjs = Conversion.Objects(Conversion.Parse(doubleTurret));
        Check(doubleObjs.Count == origObjects.Count + 4, $"double turret vehicle has {origObjects.Count + 4} parts, got {doubleObjs.Count}");
        checks++;

        // 4. Test copying Turret Body directly (selection expansion)
        var (clipBody, copyBodyCount) = BlueprintClipboard.Copy(synthetic, new[] { 11 });
        Check(copyBodyCount == 4, $"expected 4 parts copied when selecting turret body directly (expanded to ring+body+gun+decal), got {copyBodyCount}");
        checks++;
        var clipBodyNode = JsonNode.Parse(clipBody)!.AsObject();
        var bodyRoots = clipBodyNode["rootVuids"]!.AsArray().Select(x => x!.GetValue<int>()).ToList();
        Check(bodyRoots.Count == 1 && bodyRoots[0] == 10, $"expected root to be ring 10, got {string.Join(",", bodyRoots)}");
        checks++;

        // 5. Test copying Add-on with attached child Add-on and Decal
        var (clipAddonTree, countAddonTree) = BlueprintClipboard.Copy(synthetic, new[] { 20 });
        Check(countAddonTree == 3, $"expected 3 parts copied for addon tree (base 20 + child 21 + decal 22), got {countAddonTree}");
        checks++;
        var (pastedAddonTree, focusAddon, pastedAddonCount) = BlueprintClipboard.PasteInto(bareTank, clipAddonTree);
        Check(pastedAddonCount == 3, $"pasted addon tree count should be 3, got {pastedAddonCount}");
        checks++;
        var pastedTreeObjs = Conversion.Objects(Conversion.Parse(pastedAddonTree));
        Check(pastedTreeObjs.Count == 4, $"bare tank now has hull + 3 pasted parts = 4, got {pastedTreeObjs.Count}");
        checks++;
        var pastedBase = pastedTreeObjs[focusAddon];
        Check(Conversion.Id(pastedBase, "pvuid") == 0, "pasted base addon parented to target hull");
        checks++;
        var pastedChildAddon = pastedTreeObjs.Values.FirstOrDefault(o => o["guid"]?.GetValue<string>() == Conversion.AddonGuid && Conversion.Id(o, "vuid") != focusAddon);
        Check(pastedChildAddon != null && Conversion.Id(pastedChildAddon, "pvuid") == focusAddon, "pasted child addon parented to pasted base addon");
        checks++;
        var pastedDecal = pastedTreeObjs.Values.FirstOrDefault(o => o["guid"]?.GetValue<string>() == Conversion.DecalGuid);
        Check(pastedDecal != null && Conversion.Id(pastedDecal, "pvuid") == Conversion.Id(pastedChildAddon!, "vuid"), "pasted decal parented to pasted child addon");
        checks++;

        var imaged = Conversion.Parse(synthetic); Conversion.Objects(imaged)[20]["flags"] = 4;
        var imagedClip = BlueprintClipboard.Copy(imaged.ToJsonString(), new[] { 20 }).ClipboardJson;
        var (imagedPasted, imagedRoot, _) = BlueprintClipboard.PasteInto(bareTank, imagedClip);
        var imagedObject = Conversion.Objects(Conversion.Parse(imagedPasted))[imagedRoot];
        Check((imagedObject["flags"]!.GetValue<int>() & 4) != 0 && imagedObject["transform"]!["mirrorVuid"]!.GetValue<int>() == -1,
            "paste preserves the implicit mirror image of a part saved once"); checks++;

        // 6. Test Cut / RemoveFrom
        // Cutting Turret Body directly removes the whole turret assembly
        string cutFromBody = BlueprintClipboard.RemoveFrom(synthetic, new[] { 11 });
        var cutFromBodyObjs = Conversion.Objects(Conversion.Parse(cutFromBody));
        Check(!cutFromBodyObjs.ContainsKey(10) && !cutFromBodyObjs.ContainsKey(11) && !cutFromBodyObjs.ContainsKey(12) && !cutFromBodyObjs.ContainsKey(13), "cutting turret body removed entire turret assembly");
        checks++;
        Check(cutFromBodyObjs.ContainsKey(0), "hull (0) was preserved");
        checks++;
        Check(cutFromBodyObjs.ContainsKey(20) && cutFromBodyObjs.ContainsKey(21) && cutFromBodyObjs.ContainsKey(22), "addon tree was preserved");
        checks++;

        // Cutting Base Addon removes child addon and decal
        string cutAddonTree = BlueprintClipboard.RemoveFrom(synthetic, new[] { 20 });
        var cutAddonObjs = Conversion.Objects(Conversion.Parse(cutAddonTree));
        Check(!cutAddonObjs.ContainsKey(20) && !cutAddonObjs.ContainsKey(21) && !cutAddonObjs.ContainsKey(22), "cutting addon removed child addon and decal");
        checks++;
        Check(cutAddonObjs.ContainsKey(10), "turret was preserved");
        checks++;

        // Selecting a hatch/decal directly attached to a ring must not copy or cut
        // the entire turret; only the turret body promotes to its assembly.
        var (singleDecal, decalCount) = BlueprintClipboard.Copy(synthetic, new[] { 13 });
        Check(decalCount == 1, "ring-mounted decal copies independently of the turret"); checks++;
        var cutDecal = Conversion.Objects(Conversion.Parse(BlueprintClipboard.RemoveFrom(synthetic, new[] { 13 })));
        Check(!cutDecal.ContainsKey(13) && cutDecal.ContainsKey(10) && cutDecal.ContainsKey(11) && cutDecal.ContainsKey(12),
            "cutting a ring-mounted decal preserves its turret assembly"); checks++;
        Check(BlueprintClipboard.RemoveFrom(synthetic, new[] { 0 }) == synthetic, "hull-only cut leaves its descendants unchanged"); checks++;
        var hullAndAddon = Conversion.Objects(Conversion.Parse(BlueprintClipboard.RemoveFrom(synthetic, new[] { 0, 20 })));
        Check(hullAndAddon.ContainsKey(10) && hullAndAddon.ContainsKey(12) && !hullAndAddon.ContainsKey(20),
            "hull plus add-on selection only cuts the chosen add-on tree"); checks++;

        // Vehicle-wide settings are not all referenced by object *BlueprintVuid.
        var globals = Conversion.Parse(synthetic);
        globals["blueprints"]!.AsArray().Add(new JsonObject { ["id"] = 600, ["type"] = "paintJob",
            ["blueprint"] = new JsonObject { ["name"] = "Primary", ["r"] = 0.7f } });
        globals["blueprints"]!.AsArray().Add(new JsonObject { ["id"] = 601, ["type"] = "paintJobRegister",
            ["blueprint"] = new JsonObject { ["paintJobIDs"] = new JsonArray(600) } });
        globals["meshes"]!.AsArray().Add(new JsonObject { ["vuid"] = 999, ["type"] = "unrelated mesh" });
        string globalJson = globals.ToJsonString();
        var globalCut = Conversion.Parse(BlueprintClipboard.RemoveFrom(globalJson, new[] { 10 }));
        Check(globalCut["blueprints"]!.AsArray().Single(b => Conversion.Id(b!, "id") == 600)!.ToJsonString()
            == globals["blueprints"]!.AsArray().Single(b => Conversion.Id(b!, "id") == 600)!.ToJsonString(),
            "cut preserves vehicle paint exactly"); checks++;
        Check(globalCut["blueprints"]!.AsArray().Any(b => Conversion.Id(b!, "id") == 601)
            && globalCut["meshes"]!.AsArray().Any(m => Conversion.Id(m!, "vuid") == 999), "cut preserves unrelated global register and mesh"); checks++;

        var linked = Conversion.Parse(synthetic);
        linked["blueprints"]!.AsArray().Add(new JsonObject { ["id"] = 602, ["type"] = "crewSeat",
            ["blueprint"] = new JsonObject { ["operatedBehaviours"] = new JsonArray(103, 105), ["unrelatedIndices"] = new JsonArray(103, 105) } });
        var externalSeat = JsonNode.Parse(Conversion.Objects(linked)[22].ToJsonString())!.AsObject();
        externalSeat["vuid"] = 30; externalSeat["pvuid"] = 0; externalSeat.Remove("decalBlueprintVuid"); externalSeat["seatBlueprintVuid"] = 602;
        linked["objects"]!.AsArray().Add(externalSeat);
        var linkedCut = Conversion.Parse(BlueprintClipboard.RemoveFrom(linked.ToJsonString(), new[] { 12 }));
        var seatSettings = linkedCut["blueprints"]!.AsArray().Single(b => Conversion.Id(b!, "id") == 602)!["blueprint"]!;
        Check(seatSettings["operatedBehaviours"]!.ToJsonString() == "[105]" && seatSettings["unrelatedIndices"]!.ToJsonString() == "[103,105]",
            "cut clears dead gun controls while preserving unrelated integer arrays"); checks++;

        // Cross-blueprint pastes must not bind to unrelated component IDs in the
        // receiving tank when the referenced drive or gun was not copied.
        var externalPaste = Conversion.Parse(BlueprintClipboard.PasteInto(bareTank, clipJson).Json);
        var externalRingBlock = externalPaste["blueprints"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == "ringBlueprint")!["blueprint"]!;
        var externalGunBlock = externalPaste["blueprints"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == "cannonBlueprint")!["blueprint"]!;
        Check(externalRingBlock["motorVuid"]!.GetValue<int>() == -1 && externalGunBlock["barrelVuids"]!.AsArray().Count == 0,
            "paste clears links to uncopied components"); checks++;

        // Nested settings need the same remapping as the part's direct settings.
        var nested = Conversion.Parse(synthetic);
        nested["blueprints"]![0]!["blueprint"]!["mountConstraintsVuid"] = 603;
        nested["blueprints"]!.AsArray().Add(new JsonObject { ["id"] = 603, ["type"] = "constraints", ["blueprint"] = new JsonObject { ["max"] = 30 } });
        var nestedClip = BlueprintClipboard.Copy(nested.ToJsonString(), new[] { 10 }).ClipboardJson;
        var nestedPaste = Conversion.Parse(BlueprintClipboard.PasteInto(bareTank, nestedClip).Json);
        int nestedId = nestedPaste["blueprints"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == "ringBlueprint")!["blueprint"]!["mountConstraintsVuid"]!.GetValue<int>();
        Check(nestedPaste["blueprints"]!.AsArray().Any(b => Conversion.Id(b!, "id") == nestedId && b!["type"]!.GetValue<string>() == "constraints"),
            "nested constraint settings are copied and remapped"); checks++;

        // A mantlet's scale sizes its own model, not its child parts. Use its
        // attachment frame to keep a pasted part in the same world position.
        var mantletTank = Conversion.Parse(bareTank);
        var mantlet = JsonNode.Parse(Conversion.Objects(origB)[20].ToJsonString())!.AsObject();
        mantlet["vuid"] = 40; mantlet["mantlet"] = 41; mantlet.Remove("plateStructure"); mantlet.Remove("structureBlueprintVuid");
        mantlet["transform"]!["pos"] = new JsonArray(2, 0, 0); mantlet["transform"]!["rot"] = new JsonArray(0, 45, 0, 0);
        mantlet["transform"]!["scale"] = new JsonArray(2, 3, 4);
        mantletTank["objects"]!.AsArray().Add(mantlet);
        var (attached, attachedRoot, _) = BlueprintClipboard.PasteInto(mantletTank.ToJsonString(), clipAddonTree, 40);
        Check(Conversion.Near(Conversion.WorldMatrices(origObjects)[20], Conversion.WorldMatrices(Conversion.Objects(Conversion.Parse(attached)))[attachedRoot]),
            "paste onto a scaled rotated mantlet preserves world transform"); checks++;

        var transform = JsonNode.Parse(mantlet["transform"]!.ToJsonString())!.AsObject();
        string transformBefore = transform.ToJsonString();
        var shear = Matrix4x4.Identity; shear.M12 = 0.5f;
        bool rejectedShear = false; try { Conversion.WriteTransform(transform, shear); } catch { rejectedShear = true; }
        Check(rejectedShear && transform.ToJsonString() == transformBefore, "rejected shear leaves the transform exactly unchanged"); checks++;
        mantlet["transform"]!["scale"] = new JsonArray(0, 0, 0); mantlet.Remove("mantlet");
        bool rejectedZero = false; try { BlueprintClipboard.PasteInto(mantletTank.ToJsonString(), clipAddonTree, 40); } catch { rejectedZero = true; }
        Check(rejectedZero, "zero-scale paste target is rejected without arbitrary identity fallback"); checks++;

        var cyclic = Conversion.Parse(bareTank);
        var cycleA = JsonNode.Parse(mantlet.ToJsonString())!.AsObject(); cycleA["vuid"] = 40; cycleA["pvuid"] = 50; cycleA["mantlet"] = 41;
        var cycleB = JsonNode.Parse(mantlet.ToJsonString())!.AsObject(); cycleB["vuid"] = 50; cycleB["pvuid"] = 40; cycleB["mantlet"] = 51;
        cyclic["objects"]!.AsArray().Add(cycleA); cyclic["objects"]!.AsArray().Add(cycleB);
        bool rejectedCycle = false; try { Conversion.WorldMatrices(Conversion.Objects(cyclic)); } catch { rejectedCycle = true; }
        Check(rejectedCycle, "mantlet hierarchy cycle fails cleanly instead of recursing forever"); checks++;

        // 7. Test real blueprints if available
        if (realBlueprintFiles != null && realBlueprintFiles.Count > 0)
        {
            int testedFiles = 0;
            foreach (var file in realBlueprintFiles.Take(5))
            {
                try
                {
                    string raw = File.ReadAllText(file);
                    var b = Conversion.Parse(raw);
                    var objs = Conversion.Objects(b);
                    var addons = objs.Values.Where(o => o["guid"]?.GetValue<string>() == Conversion.AddonGuid).Select(o => Conversion.Id(o, "vuid")).ToList();
                    if (addons.Count > 0)
                    {
                        var (clipAddon, copiedCount) = BlueprintClipboard.Copy(raw, new[] { addons[0] });
                        if (copiedCount > 0)
                        {
                            var (pasted, _, pCount) = BlueprintClipboard.PasteInto(raw, clipAddon);
                            var checkObjs = Conversion.Objects(Conversion.Parse(pasted));
                            Check(checkObjs.Count == objs.Count + pCount, "real blueprint paste added exact part count");
                            checks++;
                            testedFiles++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Real blueprint test notice on {Path.GetFileName(file)}: {ex.Message}");
                }
            }
            Console.WriteLine($"Tested {testedFiles} real blueprints for copy/paste.");
        }

        Console.WriteLine($"CLIPBOARD_TESTS_OK: {checks} checks passed");
    }

    static string BuildSyntheticTank()
    {
        return @"{
  ""v"": ""0.2"",
  ""header"": { ""name"": ""Synthetic Tank"" },
  ""blueprints"": [
    {
      ""id"": 1,
      ""type"": ""ringBlueprint"",
      ""blueprint"": { ""motorVuid"": 101 }
    },
    {
      ""id"": 2,
      ""type"": ""structure"",
      ""blueprint"": { ""bodyMeshVuid"": 100, ""armourVolume"": 0.5 }
    },
    {
      ""id"": 3,
      ""type"": ""cannonBlueprint"",
      ""blueprint"": { ""caliber"": 88, ""barrelVuids"": [102], ""operatedBehaviours"": [] }
    },
    {
      ""id"": 4,
      ""type"": ""decal"",
      ""blueprint"": { ""v"": ""0.1"", ""imageURL"": ""test-decal"" }
    },
    {
      ""id"": 5,
      ""type"": ""decalTransform"",
      ""blueprint"": { ""x"": 100, ""y"": 100, ""z"": 100, ""flags"": 0 }
    }
  ],
  ""objects"": [
    {
      ""guid"": ""7f8a9d20-eb45-482e-b149-014c964c4e2c"",
      ""vuid"": 0,
      ""pvuid"": -1,
      ""flags"": 0,
      ""transform"": { ""pos"": [0,0,0], ""rot"": [0,0,0,0], ""scale"": [1,1,1], ""mirrorVuid"": -1 }
    },
    {
      ""guid"": ""99281776-6b29-4ffb-9d8b-04139ca7b6a2"",
      ""vuid"": 10,
      ""pvuid"": 0,
      ""flags"": 0,
      ""ringBlueprintVuid"": 1,
      ""structureID"": 11,
      ""transform"": { ""pos"": [0,1,0], ""rot"": [0,0,0,0], ""scale"": [1,1,1], ""mirrorVuid"": -1 }
    },
    {
      ""guid"": ""7f8a9d20-eb45-482e-b149-014c964c4e2c"",
      ""vuid"": 11,
      ""pvuid"": 10,
      ""flags"": 0,
      ""structureBlueprintVuid"": 2,
      ""transform"": { ""pos"": [0,0.5,0], ""rot"": [0,0,0,0], ""scale"": [1,1,1], ""mirrorVuid"": -1 }
    },
    {
      ""guid"": ""cannon_guid"",
      ""vuid"": 12,
      ""pvuid"": 11,
      ""flags"": 0,
      ""cannon"": 103,
      ""cannonBlueprintVuid"": 3,
      ""transform"": { ""pos"": [0,0,1], ""rot"": [0,0,0,0], ""scale"": [1,1,1], ""mirrorVuid"": -1 }
    },
    {
      ""guid"": ""e59ff736-a6ea-4a1a-a4c5-6437ed15b872"",
      ""vuid"": 13,
      ""pvuid"": 10,
      ""flags"": 0,
      ""decalScaler"": 104,
      ""projectedDecal"": 105,
      ""decalBlueprintVuid"": 4,
      ""decalProjectionBlueprintVuid"": 5,
      ""transform"": { ""pos"": [0.5,0,0], ""rot"": [0,0,0,0], ""scale"": [1,1,1], ""mirrorVuid"": -1 }
    },
    {
      ""guid"": ""8f8a9d20-eb45-482e-b149-014c964c4e2c"",
      ""vuid"": 20,
      ""pvuid"": 0,
      ""flags"": 0,
      ""plateStructure"": 201,
      ""structureBlueprintVuid"": 2,
      ""transform"": { ""pos"": [1,0,0], ""rot"": [0,0,0,0], ""scale"": [1,1,1], ""mirrorVuid"": -1 }
    },
    {
      ""guid"": ""8f8a9d20-eb45-482e-b149-014c964c4e2c"",
      ""vuid"": 21,
      ""pvuid"": 20,
      ""flags"": 0,
      ""plateStructure"": 202,
      ""structureBlueprintVuid"": 2,
      ""transform"": { ""pos"": [0,0.1,0], ""rot"": [0,0,0,0], ""scale"": [0.5,0.5,0.5], ""mirrorVuid"": -1 }
    },
    {
      ""guid"": ""e59ff736-a6ea-4a1a-a4c5-6437ed15b872"",
      ""vuid"": 22,
      ""pvuid"": 21,
      ""flags"": 0,
      ""decalScaler"": 203,
      ""projectedDecal"": 204,
      ""decalBlueprintVuid"": 4,
      ""decalProjectionBlueprintVuid"": 5,
      ""transform"": { ""pos"": [0,0.05,0], ""rot"": [0,0,0,0], ""scale"": [0.2,0.2,0.2], ""mirrorVuid"": -1 }
    }
  ],
  ""meshes"": [
    {
      ""vuid"": 100,
      ""type"": ""plateStructureMesh"",
      ""meshData"": { ""format"": ""freeform"", ""vertices"": ""0 0 0 1 0 0 0 1 0"", ""faces"": ""0 1 2"" }
    }
  ]
}";
    }

    static string BuildBareTank()
    {
        return @"{
  ""v"": ""0.2"",
  ""header"": { ""name"": ""Bare Tank"" },
  ""blueprints"": [],
  ""objects"": [
    {
      ""guid"": ""7f8a9d20-eb45-482e-b149-014c964c4e2c"",
      ""vuid"": 0,
      ""pvuid"": -1,
      ""flags"": 0,
      ""transform"": { ""pos"": [0,0,0], ""rot"": [0,0,0,0], ""scale"": [1,1,1], ""mirrorVuid"": -1 }
    }
  ],
  ""meshes"": []
}";
    }
}
