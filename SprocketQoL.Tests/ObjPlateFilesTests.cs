using SprocketQoL;

static class ObjPlateFilesTests
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool ok, string message) { checks++; if (!ok) throw new Exception("OBJ library files: " + message); }
        foreach (string name in new[] { "../escape", "..\\escape", "C:\\test", "a<b>c:d\"e|f?g*h", "control\nname" })
        {
            string safe = ObjPlateFiles.BlueprintName(name);
            Check(safe.IndexOfAny("<>:\"/\\|?*\n".ToCharArray()) == -1, "unsafe filename characters removed");
            Check(safe != "." && safe != "..", "name cannot escape the destination");
        }
        foreach (string reserved in new[] { "CON", "nul.txt", "COM1", "LPT9", "COM\u00b9", "CONIN$" })
            Check(ObjPlateFiles.BlueprintName(reserved).StartsWith('_'), "Windows device names are escaped");
        Check(ObjPlateFiles.BlueprintName(" tank... ") == "tank", "trailing dots and spaces removed");
        Check(ObjPlateFiles.BlueprintName("\u76d4\u7532") == "\u76d4\u7532", "Unicode names preserved");
        Check(ObjPlateFiles.BlueprintName(new string('x', 200)).Length == 100, "filename length bounded");
        bool emptyRejected = false;
        try { ObjPlateFiles.BlueprintName(" ... "); } catch (FormatException) { emptyRejected = true; }
        Check(emptyRejected, "empty name rejected");
        string workspace = Path.Combine(Path.GetTempPath(), "qol-obj-library-test-" + Guid.NewGuid().ToString("N"));
        string faction = Path.Combine(workspace, "Factions", "Test faction");
        Directory.CreateDirectory(faction);
        try
        {
            string destination = Path.Combine(faction, "Blueprints", "Plate Structures");
            Check(ObjPlateFiles.Destination(faction) == destination, "native Plate Structures destination");
            const string data = "{\"v\":\"0.2\",\"name\":\"\u76d4\u7532\",\"mesh\":{}}";
            string first = ObjPlateFiles.Save(faction, "Model", data);
            Check(first == Path.Combine(destination, "Model.blueprint"), "native extension and folder");
            Check(File.ReadAllText(first) == data, "JSON and Unicode saved exactly");
            Check(!File.ReadAllBytes(first).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF8 without BOM");
            string second = ObjPlateFiles.Save(faction, "Model", "second");
            Check(Path.GetFileName(second) == "Model (2).blueprint", "collision gets a new filename");
            Check(File.ReadAllText(first) == data, "existing blueprint preserved");
            Directory.CreateDirectory(Path.Combine(destination, "Model (3).blueprint"));
            string fourth = ObjPlateFiles.Save(faction, "Model", "fourth");
            Check(Path.GetFileName(fourth) == "Model (4).blueprint", "directory collisions preserved");
            string traversal = ObjPlateFiles.Save(faction, "../../Other", "inside");
            Check(Path.GetDirectoryName(traversal) == destination, "filename traversal stays in Plate Structures");
            Check(!Directory.EnumerateFiles(destination, "*.tmp").Any(), "no temporary files remain");
            bool missingRejected = false;
            try { ObjPlateFiles.Save(Path.Combine(workspace, "missing"), "Model", data); }
            catch (DirectoryNotFoundException) { missingRejected = true; }
            Check(missingRejected && !Directory.Exists(Path.Combine(workspace, "missing")), "missing faction never created");
        }
        finally { Directory.Delete(workspace, true); } // only this test's freshly created GUID directory
        Console.WriteLine($"OBJ_PLATE_FILES_TESTS_OK: {checks} checks");
    }
}
