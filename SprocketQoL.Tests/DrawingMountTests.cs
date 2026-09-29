using SprocketQoL;
using System.Text.Json.Nodes;

static class DrawingMountTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("drawing mounts: " + why); }
        // RGM-167's 105 mm turret sits below the 20 mm trunnions in the attachment tree.
        var parts = new Dictionary<int, DrawingMounts.Part>
        {
            [0] = new(-1,false,false), [255] = new(0,false,false), [270] = new(255,true,false),
            [281] = new(270,false,false), [253] = new(270,false,false), [337] = new(253,false,false),
            [419] = new(337,false,true), [426] = new(419,false,false), [458] = new(426,false,false),
            [463] = new(458,true,false), [474] = new(463,false,false)
        };
        var guns = new[] { (Id:281,Caliber:20), (Id:474,Caliber:105) };
        var preferences = new GunAnnotationPreferences(null);
        preferences.Set(GunAnnotationPreferences.Key("RGM-167", 281), false);
        bool Included(int id) => preferences.Shows(GunAnnotationPreferences.Key("RGM-167", id));
        Check(DrawingMounts.Owner(281,parts)==270, "20 mm belongs to its own trunnions");
        Check(DrawingMounts.Owner(474,parts)==463, "105 mm belongs only to nearest trunnions");
        Check(DrawingMounts.SelectGun(270,guns,parts,Included)==null, "disabled 20 mm cannot borrow nested 105 mm for its +/-5 degree annotations");
        Check(DrawingMounts.SelectGun(463,guns,parts,Included)==474, "105 mm keeps its own +15/-8 degree annotations");
        preferences.Set(GunAnnotationPreferences.Key("RGM-167",281),true);
        Check(DrawingMounts.SelectGun(270,guns,parts,Included)==281, "reenabled 20 mm uses its actual barrel, not the larger nested gun");
        preferences.Set(GunAnnotationPreferences.Key("RGM-167",474),false);
        Check(DrawingMounts.SelectGun(463,guns,parts,Included)==null, "disabled inner gun cannot borrow outer gun");
        parts[500]=new(463,false,false);
        Check(DrawingMounts.SelectGun(463,guns.Append((500,7)),parts,Included)==500, "enabled coax on same mount remains eligible");
        parts[501]=new(426,false,false);
        Check(DrawingMounts.Owner(501,parts)==-1, "direct turret gun does not borrow outer trunnions");
        parts[502]=new(503,false,false); parts[503]=new(502,false,false);
        Check(DrawingMounts.Owner(502,parts)==-1 && DrawingMounts.Owner(999,parts)==-1, "cycle and missing part fail safely");

        // Read-only verification against the supplied tank, when available on this workstation.
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "Sprocket", "Factions", "PMC", "Blueprints", "Vehicles", "RGM-167.blueprint");
        if (File.Exists(file))
        {
            string original = File.ReadAllText(file);
            var bp = JsonNode.Parse(original)!;
            var objects = bp["objects"]!.AsArray().Select(o=>o!).ToArray();
            var actual = objects.ToDictionary(o=>o["vuid"]!.GetValue<int>(), o=>new DrawingMounts.Part(
                o["pvuid"]!.GetValue<int>(), o["guid"]!.GetValue<string>()==DrawingMounts.TrunnionGuid,
                o["guid"]!.GetValue<string>()==Conversion.RingGuid));
            var blueprints = bp["blueprints"]!.AsArray().ToDictionary(b=>b!["id"]!.GetValue<int>(),b=>b!["blueprint"]!);
            var actualGuns = objects.Where(o=>o["cannon"]!=null).Select(o=>(
                Id:o["vuid"]!.GetValue<int>(), Caliber:blueprints[o["cannonBlueprintVuid"]!.GetValue<int>()]["caliber"]!.GetValue<int>())).ToArray();
            Check(DrawingMounts.SelectGun(270,actualGuns,actual,id=>id!=281)==null, "real tank: excluded outer mount produces no annotations");
            Check(DrawingMounts.SelectGun(463,actualGuns,actual,id=>id!=281)==474, "real tank: only intended 105 mm remains selected");
            Check(DrawingMounts.SelectGun(270,actualGuns,actual,_=>true)==281, "real tank: enabled outer mount uses 20 mm");
            Check(original==File.ReadAllText(file), "source blueprint unchanged");
            Console.WriteLine("DRAWING_MOUNTS_REAL_TANK_OK: RGM-167, 20 mm excluded, 105 mm retained");
        }
        Console.WriteLine($"DRAWING_MOUNT_TESTS_OK: {checks} checks");
    }
}
