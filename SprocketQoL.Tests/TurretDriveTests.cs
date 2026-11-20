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

        // Observed native empty-twin form: both rings are marked mirrored, while the twin
        // initially aliases the source body and drive. Completion must create two owned parts.
        {
            var b = Fixture(); var os = Conversion.Objects(b);
            os[100]["flags"] = 6; b["objects"]!.AsArray().Remove(os[40]);
            string original = b.ToJsonString();
            var initial = Conversion.RepairMirroredTurretDrives(original);
            Check(initial.Repaired == 0 && initial.Unresolved == 1 && initial.Json == original, "empty twin is not repaired by sharing its source drive");
            var result = Conversion.MirrorTurret(original, 10);
            var after = Conversion.Parse(result.Json); Validate(after);
            var all = Conversion.Objects(after); int body = all[100]["structureID"]!.GetValue<int>();
            Check(result.Mirrored == 2 && all.Count == b["objects"]!.AsArray().Count + 2, "empty twin adds its body and motor only");
            Check(body != 20 && Conversion.Id(all[body], "pvuid") == 100, "native empty twin acquires its own body");
            Check(b.ToJsonString() == original, "empty twin completion does not mutate input");
        }

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

        // Filling a partially mirrored assembly must also repair either ring's existing cross-connection.
        // Both drives already exist; merely remapping the new seat/twin ring can leave the source on the twin's motor.
        foreach (string kind in new[] { "shared-block", "left-to-right", "right-to-left", "crossed" })
        {
            var b = Mirrored(); var os = Conversion.Objects(b); int seatTwin = os[40]["transform"]!["mirrorVuid"]!.GetValue<int>();
            b["objects"]!.AsArray().Remove(os[seatTwin]); os[40]["transform"]!["mirrorVuid"] = -1;
            int left = Motor(b, 10), right = Motor(b, 100);
            if (kind == "shared-block") os[100]["ringBlueprintVuid"] = os[10]["ringBlueprintVuid"]!.GetValue<int>();
            if (kind is "left-to-right" or "crossed") Block(b, os[10])["motorVuid"] = right;
            if (kind is "right-to-left" or "crossed") Block(b, os[100])["motorVuid"] = left;
            string original = b.ToJsonString();
            var result = Conversion.MirrorTurret(b.ToJsonString(), 10);
            var after = Conversion.Parse(result.Json); Validate(after);
            Check(result.Mirrored == 1, kind + " partial mirror copies only the missing seat");
            Check(b.ToJsonString() == original, kind + " partial mirror does not mutate input");
            Check(Motor(after, 10) == left && Motor(after, 100) == right, kind + " partial mirror keeps both drives local");
            var all = Conversion.Objects(after); int addedSeat = all[40]["transform"]!["mirrorVuid"]!.GetValue<int>();
            var seatBlock = after["blueprints"]!.AsArray().Single(x => x!["id"]!.GetValue<int>() == all[addedSeat]["seatBlueprintVuid"]!.GetValue<int>())!;
            Check(seatBlock["blueprint"]!["operatedBehaviours"]![1]!.GetValue<int>() == Motor(after, 100), "partial mirror seat uses existing copied motor");
            foreach (var (id, part) in os.Where(pair => pair.Key != seatTwin))
            {
                Check(part["pvuid"]!.GetValue<int>() == all[id]["pvuid"]!.GetValue<int>(), kind + " partial mirror retains existing hierarchy");
                if (id != 40) Check(part["transform"]!.ToJsonString() == all[id]["transform"]!.ToJsonString(), kind + " partial mirror retains existing transforms");
            }
            var again = Conversion.RepairMirroredTurretDrives(result.Json);
            Check(again.Repaired == 0 && again.Json == result.Json, kind + " partial mirror repair is complete");
        }
        {
            var b = Mirrored(); var os = Conversion.Objects(b);
            int seatTwin = os[40]["transform"]!["mirrorVuid"]!.GetValue<int>();
            b["objects"]!.AsArray().Remove(os[seatTwin]); os[40]["transform"]!["mirrorVuid"] = -1;
            Block(b, os[100])["motorVuid"] = 999;
            var after = Conversion.Parse(Conversion.MirrorTurret(b.ToJsonString(), 10).Json);
            Check(Motor(after, 10) == 31 && Motor(after, 100) == 999, "partial mirror preserves an intentional external drive reference");
        }

        // A nested turret carries its own drive. Both ring blueprint references must be remapped independently.
        foreach (bool modernBodyReference in new[] { false, true })
        {
            var b = Fixture(); var objects = b["objects"]!.AsArray();
            var ring = Part(50, 20, Conversion.RingGuid); ring["turretRing"] = 51; ring["structureID"] = 60; ring["ringBlueprintVuid"] = 3;
            if (modernBodyReference) { ring.Remove("structureID"); ring["compartmentBodyID"] = new JsonObject { ["structureVuid"] = 60 }; }
            var body = Part(60, 50, Conversion.CompartmentGuid); var drive = Part(70, 60, Conversion.MotorGuid); drive["motor"] = 71;
            objects.Add(ring); objects.Add(body); objects.Add(drive);
            b["blueprints"]!.AsArray().Add(new JsonObject { ["id"] = 3, ["type"] = "turretRing", ["blueprint"] = new JsonObject { ["motorVuid"] = 71 } });
            var after = Conversion.Parse(Conversion.MirrorTurret(b.ToJsonString(), 10).Json); Validate(after);
            Check(Motor(after, 10) == 31 && Motor(after, 50) == 71, "nested source drives unchanged");
            var all = Conversion.Objects(after);
            int nestedTwin = all[50]["transform"]!["mirrorVuid"]!.GetValue<int>();
            int bodyReference = modernBodyReference ? all[nestedTwin]["compartmentBodyID"]!["structureVuid"]!.GetValue<int>()
                : all[nestedTwin]["structureID"]!.GetValue<int>();
            Check(Conversion.Id(all[bodyReference], "pvuid") == nestedTwin, "nested copied ring references its own copied body");
        }
        {
            var b = Fixture(); var all = Conversion.Objects(b); all[100]["transform"]!["mirrorVuid"] = -1;
            bool rejected = false; try { Conversion.MirrorTurret(b.ToJsonString(), 10); } catch { rejected = true; }
            Check(rejected, "non-reciprocal mirror link fails without redirecting parts");
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

        // The twin's place as the turret mirror keeper sets it: on twin parents mirrored in the parent's frame; beside its
        // partner on one parent (rings on an off-centre, tilted add-on), the vehicle-space mirror of the partner.
        var rot = System.Numerics.Quaternion.Normalize(new(0.1f, 0.3f, -0.2f, 0.9f));
        var (p1, q1) = Conversion.MirrorPose(new(0.4f, 1, -2), rot);
        Check(p1 == new System.Numerics.Vector3(-0.4f, 1, -2) && q1 == new System.Numerics.Quaternion(rot.X, -rot.Y, -rot.Z, rot.W), "mirrored on twin parents: x and the turn's y, z change sign");
        var parent = (Pos: new System.Numerics.Vector3(0.666f, 0.71f, -0.28f), Rot: System.Numerics.Quaternion.CreateFromYawPitchRoll(0.02f, 0.09f, 0.01f));
        var leaderPos = new System.Numerics.Vector3(-0.124f, 0.013f, -0.06f);
        var (fp, fq) = Conversion.MirrorPose(leaderPos, rot, parent);
        var leaderWorld = parent.Pos + System.Numerics.Vector3.Transform(leaderPos, parent.Rot);
        var followerWorld = parent.Pos + System.Numerics.Vector3.Transform(fp, parent.Rot);
        var leaderTurn = parent.Rot * rot; var followerTurn = parent.Rot * fq;
        Check(System.Numerics.Vector3.Distance(followerWorld, new(-leaderWorld.X, leaderWorld.Y, leaderWorld.Z)) < 1e-5f, "on one parent: the twin is at the mirror of its partner across the vehicle's centre");
        Check(Math.Abs(System.Numerics.Quaternion.Dot(followerTurn, new(leaderTurn.X, -leaderTurn.Y, -leaderTurn.Z, leaderTurn.W))) > 0.99999f, "on one parent: the twin is turned as the mirror of its partner");
        var (bp, bq) = Conversion.MirrorPose(fp, fq, parent);
        Check(System.Numerics.Vector3.Distance(bp, leaderPos) < 1e-5f && Math.Abs(System.Numerics.Quaternion.Dot(bq, rot)) > 0.99999f, "mirroring the twin back gives the partner");
        // Review DATA-02: the twin turret's copies keep the own paint of the parts they copy.
        var painted = Fixture();
        painted["blueprints"]!.AsArray().Add(JsonNode.Parse("""{"id":50,"type":"paintJob","blueprint":{"name":"Green","description":"Quality of Life parts: 20 40"}}"""));
        var twin = Conversion.Parse(Conversion.MirrorTurret(painted.ToJsonString(), 10).Json);
        var copies = Conversion.Objects(twin).Values.Where(o => o["transform"]!["mirrorVuid"]!.GetValue<int>() is 20 or 40).Select(o => o["vuid"]!.GetValue<int>()).ToList();
        string owners = twin["blueprints"]!.AsArray().Single(x => x!["id"]!.GetValue<int>() == 50)!["blueprint"]!["description"]!.GetValue<string>();
        Check(copies.Count == 2 && copies.All(c => owners.Split(' ').Contains(c.ToString())) && owners.Contains(" 20") && owners.Contains(" 40"),
            "the twin turret's body and seat copies keep their own paint");
        // An Alt-copied turret (KHeavy, Discord 2026-11-16): the copy's ring still links the original's body and motor,
        // sharing its settings block. The copy gets its own; the original and an empty mirror twin are left as they are.
        var copied = Fixture();
        var copyRing = Part(200, 0, Conversion.RingGuid, 4); copyRing["turretRing"] = 201; copyRing["ringBlueprintVuid"] = 1; copyRing["structureID"] = 20;
        var copyBody = Part(210, 200, Conversion.CompartmentGuid); copyBody["plateStructure"] = 211;
        var copyMotor = Part(220, 210, Conversion.MotorGuid); copyMotor["motor"] = 221;
        foreach (var p in new[] { copyRing, copyBody, copyMotor }) copied["objects"]!.AsArray().Add(p);
        Check(Conversion.RepairTurretLinks(copied) == 2, "a copied ring's body and motor links are both repaired");
        var cos = Conversion.Objects(copied);
        Check(Motor(copied, 200) == 221 && cos[200]["structureID"]!.GetValue<int>() == 210, "the copy links its own body and motor");
        Check(Motor(copied, 10) == 31 && cos[10]["structureID"]!.GetValue<int>() == 20, "the original keeps its links");
        Check(Motor(copied, 100) == 31 && cos[100]["structureID"]!.GetValue<int>() == 20, "a mirror twin with nothing on it is left alone");
        Check(Conversion.RepairTurretLinks(copied) == 0 && Conversion.RepairTurretLinks(Fixture()) == 0, "nothing more to repair; healthy turrets untouched");
        // The real design (TestFixtures\TurretLinks, next to the backups folder): its right turret linked the left one's.
        if (Environment.GetEnvironmentVariable("SPROCKET_QOL_BACKUPS") is { Length: > 0 } backups
            && Path.Combine(backups, "..", "TurretLinks", "KHeavy.blueprint") is var kheavy && File.Exists(kheavy))
        {
            var health = Conversion.Health(File.ReadAllText(kheavy));
            Check(health.Turrets == 2 && health.TurretLinks == 2 && health.Fixable >= 2 && health.Summary.Contains("2 turret ring link(s)") && health.NonFinite == 0,
                "KHeavy: the design check reports both broken links");
            var real = Conversion.Parse(File.ReadAllText(kheavy));
            var fixedJson = real.ToJsonString();
            Check(Conversion.RepairTurretLinks(real) == 2, "KHeavy: the copied turret's two links repaired");
            var ros = Conversion.Objects(real);
            Check(Motor(real, 318) == 343 && ros[318]["structureID"]!.GetValue<int>() == 323 && Motor(real, 432) == 438 && ros[432]["structureID"]!.GetValue<int>() == 440,
                "KHeavy: each ring on its own motor and body");
            Check(Conversion.Health(real.ToJsonString()).Fixable == 0 && fixedJson.Length > 0, "KHeavy: nothing left to fix after the repair");
        }
        else Console.WriteLine("  (KHeavy turret links skipped: set SPROCKET_QOL_BACKUPS)");

        // Turret preview maths, in the drawing sheet's sense: +90° turns a forward muzzle to the right (+X), +10° elevation
        // raises it; a turn keeps its distance from the axis.
        var muzzle = new System.Numerics.Vector3(0, 1, 3);
        var turned = TurretPreviewMath.Turn(muzzle, System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY, 90);
        Check(System.Numerics.Vector3.Distance(turned, new(3, 1, 0)) < 1e-5f, $"preview: +90° turns the muzzle right ({turned})");
        var raised = TurretPreviewMath.Turn(muzzle, new(0, 1, 0), -System.Numerics.Vector3.UnitX, 10);
        Check(raised.Y > 1.5f && MathF.Abs(System.Numerics.Vector3.Distance(raised, new(0, 1, 0)) - 3) < 1e-5f, $"preview: +10° elevation raises the muzzle ({raised})");
        var steps = TurretPreviewMath.Angles(-130, 30, 2);
        Check(steps.Length == 81 && steps[0] == -130 && steps[^1] == 30, "preview: KHeavy's -130° to +30° in 2° steps, both ends");
        Check(TurretPreviewMath.Angles(-180, 180, 2).Length == 181 && TurretPreviewMath.Angles(-400, 400, 2).Length == 181 && TurretPreviewMath.Angles(5, 1, 2).Length == 0,
            "preview: a full turn is one turn; an empty range has no angles");
        Check(TurretPreviewMath.Full(-180, 180) && !TurretPreviewMath.Full(-130, 30), "preview: full turns draw no limit lines");
        // A barrel from 0.1 m off the axis out to 3 m leaves a 0.4 m ring a tenth of the way along; one starting outside, at once.
        float leaves = TurretPreviewMath.Outside(new(0.1f, 1, 0), new(3.1f, 0.5f, 0), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY, 0.4f);
        Check(MathF.Abs(leaves - 0.1f) < 1e-5f, $"preview: the hull check starts at the ring's edge ({leaves})");
        Check(TurretPreviewMath.Outside(new(1, 1, 0), new(3, 0, 0), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY, 0.4f) == 0
              && TurretPreviewMath.Outside(new(0, 1, 0), new(0, -1, 0), System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitY, 0.4f) == 1,
            "preview: a barrel starting outside the ring is checked whole; one straight down the axis never leaves it");
        Console.WriteLine($"TURRET_DRIVE_TESTS_OK: {checks} checks");
    }
}
