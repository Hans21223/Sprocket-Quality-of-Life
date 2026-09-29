using System.Text.Json.Nodes;
using SprocketQoL;

static class TurretDriveTests
{
    static int checks;
    static void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Turret drive: " + why); }
    static JsonObject Part(int id, int parent, string guid, float x = 0) => new()
    {
        ["vuid"] = id, ["pvuid"] = parent, ["guid"] = guid, ["flags"] = 2,
        ["transform"] = new JsonObject { ["mirrorVuid"] = -1, ["pos"] = new JsonArray(x, 0, 0),
            ["rot"] = new JsonArray(0, 0, 0, 0), ["scale"] = new JsonArray(1, 1, 1) }
    };
    static JsonObject Fixture()
    {
        var hull = Part(0, -1, Conversion.CompartmentGuid);
        var a = Part(10, 0, Conversion.RingGuid, 2); a["turretRing"] = 11; a["ringBlueprintVuid"] = 1; a["structureID"] = 20;
        var b = Part(100, 0, Conversion.RingGuid, -2); b["turretRing"] = 101; b["ringBlueprintVuid"] = 1; b["structureID"] = 20;
        a["transform"]!["mirrorVuid"] = 100; b["transform"]!["mirrorVuid"] = 10; a["flags"] = 6; b["flags"] = 7;
        var body = Part(20, 10, Conversion.CompartmentGuid);
        var motor = Part(30, 20, Conversion.MotorGuid); motor["motor"] = 31;
        var seat = Part(40, 20, "seat"); seat["crewSeat"] = 41; seat["seatBlueprintVuid"] = 2;
        return new JsonObject
        {
            ["objects"] = new JsonArray(hull, a, b, body, motor, seat), ["meshes"] = new JsonArray(),
            ["blueprints"] = new JsonArray(
                new JsonObject { ["id"] = 1, ["type"] = "turretRing", ["blueprint"] = new JsonObject { ["motorVuid"] = 31, ["ringDiameter"] = 1500 } },
                new JsonObject { ["id"] = 2, ["type"] = "crewSeat", ["blueprint"] = new JsonObject { ["operatedBehaviours"] = new JsonArray(11, 31) } })
        };
    }
    static JsonObject Block(JsonObject b, JsonObject ring) => b["blueprints"]!.AsArray().Single(x => x!["id"]!.GetValue<int>() == ring["ringBlueprintVuid"]!.GetValue<int>())!["blueprint"]!.AsObject();
    static int Motor(JsonObject b, int ring) => Block(b, Conversion.Objects(b)[ring])["motorVuid"]!.GetValue<int>();
    static JsonObject Mirrored() => Conversion.Parse(Conversion.MirrorTurret(Fixture().ToJsonString(), 10).Json);
    static void Validate(JsonObject b)
    {
        var os = Conversion.Objects(b);
        foreach (var ring in os.Values.Where(o => Conversion.GuidOf(o) == Conversion.RingGuid))
        {
            int ringId = ring["vuid"]!.GetValue<int>();
            var motor = os.Values.Single(o => o["motor"]?.GetValue<int>() == Motor(b, ringId));
            Check(os[motor["pvuid"]!.GetValue<int>()]["pvuid"]!.GetValue<int>() == ringId, "drive belongs to its own ring " + ringId);
        }
        Check(b["blueprints"]!.AsArray().Select(x => x!["id"]!.GetValue<int>()).Distinct().Count() == b["blueprints"]!.AsArray().Count, "unique setting blocks");
    }
    public static void Run()
    {
        checks = 0;
        var fresh = Mirrored(); Validate(fresh);
        Check(Motor(fresh, 10) == 31 && Motor(fresh, 100) != 31, "new mirror keeps source drive and remaps twin");
        Check(Block(fresh, Conversion.Objects(fresh)[100])["ringDiameter"]!.GetValue<int>() == 1500, "ring dimensions retained");
        var already = Conversion.RepairMirroredTurretDrives(fresh.ToJsonString());
        Check(already.Repaired == 0 && already.Json == fresh.ToJsonString(), "healthy pair is byte-for-byte unchanged");

        foreach (string kind in new[] { "shared-block", "left-to-right", "right-to-left", "crossed" })
        {
            var b = Mirrored(); var os = Conversion.Objects(b);
            int left = Motor(b, 10), right = Motor(b, 100);
            if (kind == "shared-block") os[100]["ringBlueprintVuid"] = os[10]["ringBlueprintVuid"]!.GetValue<int>();
            if (kind is "left-to-right" or "crossed") Block(b, os[10])["motorVuid"] = right;
            if (kind is "right-to-left" or "crossed") Block(b, os[100])["motorVuid"] = left;
            string original = b.ToJsonString();
            var repaired = Conversion.RepairMirroredTurretDrives(original);
            Check(repaired.Repaired == (kind == "crossed" ? 2 : 1), kind + " repair count");
            var after = Conversion.Parse(repaired.Json); Validate(after);
            Check(b.ToJsonString() == original, "input not mutated");
            Check(after["meshes"]!.ToJsonString() == b["meshes"]!.ToJsonString(), "repair preserves shapes");
            var afterOs = Conversion.Objects(after);
            foreach (var (id, o) in os)
            {
                Check(o["transform"]!.ToJsonString() == afterOs[id]["transform"]!.ToJsonString(), "repair preserves placement");
                Check(o["pvuid"]!.GetValue<int>() == afterOs[id]["pvuid"]!.GetValue<int>(), "repair preserves hierarchy");
            }
            var again = Conversion.RepairMirroredTurretDrives(repaired.Json);
            Check(again.Repaired == 0 && again.Json == repaired.Json, "repeated repair is a no-op");
        }

        // A partially filled twin already has a body and motor. The new seat must reference that motor too.
        {
            var b = Mirrored(); var os = Conversion.Objects(b); int seatTwin = os[40]["transform"]!["mirrorVuid"]!.GetValue<int>();
            b["objects"]!.AsArray().Remove(os[seatTwin]); os[40]["transform"]!["mirrorVuid"] = -1;
            os[100]["ringBlueprintVuid"] = os[10]["ringBlueprintVuid"]!.GetValue<int>();
            var result = Conversion.MirrorTurret(b.ToJsonString(), 10);
            var after = Conversion.Parse(result.Json); Validate(after);
            var all = Conversion.Objects(after); int addedSeat = all[40]["transform"]!["mirrorVuid"]!.GetValue<int>();
            var seatBlock = after["blueprints"]!.AsArray().Single(x => x!["id"]!.GetValue<int>() == all[addedSeat]["seatBlueprintVuid"]!.GetValue<int>())!;
            Check(seatBlock["blueprint"]!["operatedBehaviours"]![1]!.GetValue<int>() == Motor(after, 100), "partial mirror seat uses existing copied motor");
        }

        // A nested turret carries its own drive. Both ring blueprint references must be remapped independently.
        {
            var b = Fixture(); var objects = b["objects"]!.AsArray();
            var ring = Part(50, 20, Conversion.RingGuid); ring["turretRing"] = 51; ring["structureID"] = 60; ring["ringBlueprintVuid"] = 3;
            var body = Part(60, 50, Conversion.CompartmentGuid); var drive = Part(70, 60, Conversion.MotorGuid); drive["motor"] = 71;
            objects.Add(ring); objects.Add(body); objects.Add(drive);
            b["blueprints"]!.AsArray().Add(new JsonObject { ["id"] = 3, ["type"] = "turretRing", ["blueprint"] = new JsonObject { ["motorVuid"] = 71 } });
            var after = Conversion.Parse(Conversion.MirrorTurret(b.ToJsonString(), 10).Json); Validate(after);
            Check(Motor(after, 10) == 31 && Motor(after, 50) == 71, "nested source drives unchanged");
        }
        foreach (string kind in new[] { "missing", "ambiguous", "unlinked", "external" })
        {
            var b = Mirrored(); var os = Conversion.Objects(b);
            if (kind != "external") os[100]["ringBlueprintVuid"] = os[10]["ringBlueprintVuid"]!.GetValue<int>();
            if (kind == "missing") b["objects"]!.AsArray().Remove(os.Values.Single(o => o["motor"]?.GetValue<int>() == 31));
            if (kind == "ambiguous") { var extra = Part(900, 20, Conversion.MotorGuid); extra["motor"] = 901; b["objects"]!.AsArray().Add(extra); }
            if (kind == "unlinked") os[10]["transform"]!["mirrorVuid"] = -1;
            if (kind == "external") Block(b, os[100])["motorVuid"] = 999;
            string original = b.ToJsonString(); var result = Conversion.RepairMirroredTurretDrives(original);
            Check(result.Repaired == 0 && result.Json == original, kind + " left untouched");
        }
        Console.WriteLine($"TURRET_DRIVE_TESTS_OK: {checks} checks");
    }
}
