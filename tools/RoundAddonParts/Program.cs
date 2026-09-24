// Writes the "Round Add-on Parts" data mod: palette parts like the game's cube, but round.
//   dotnet run --project tools\RoundAddonParts -- "DataMods\Round Add-on Parts"
// No BepInEx needed: Sprocket loads every part file in Sprocket_Data\StreamingAssets\Parts.
using System.Text;
using SprocketQoL;

string outDir = args.Length > 0 ? args[0] : throw new ArgumentException("usage: RoundAddonParts <output folder>");
var parts = Directory.CreateDirectory(Path.Combine(outDir, "Sprocket_Data", "StreamingAssets", "Parts"));
var names = Directory.CreateDirectory(Path.Combine(outDir, "Sprocket_Data", "StreamingAssets", "Localization", "en-UK", "Parts"));
// Fixed GUIDs, so blueprints that use these parts keep loading after a regenerate.
foreach (var (sides, guid) in new[] { (16, "3f1c6a9e-7b2d-4e58-9c14-2a6b8d0e4f16"), (32, "3f1c6a9e-7b2d-4e58-9c14-2a6b8d0e4f32") })
{
    string name = $"roundAddon{sides}PlateStructure";
    File.WriteAllText(Path.Combine(parts.FullName, $"{name}Part.json"), AddonEdits.RoundAddonPart(guid, name, sides));
    File.WriteAllText(Path.Combine(names.FullName, $"{name}.xml"),
        "<?xml version=\"1.0\" encoding=\"utf-16\"?>\r\n" +
        "<Part xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\r\n" +
        $"  <name>Round add-on ({sides} sides)</name>\r\n" +
        $"  <description>A round add-on structure, {sides}-sided, the size of the default cube. Reshape it with the structure tools.</description>\r\n" +
        "</Part>", Encoding.Unicode); // the game's own part names are UTF-16
    Console.WriteLine($"wrote {name} ({sides} sides, guid {guid})");
}
