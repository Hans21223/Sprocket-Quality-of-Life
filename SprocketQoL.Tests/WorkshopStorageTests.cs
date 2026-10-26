using System.Text.Json;
using System.Text.Json.Nodes;
using SprocketQoL;

static class WorkshopStorageTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new Exception("Workshop: " + reason); }
        void Throws(Action action, string reason) { bool failed = false; try { action(); } catch { failed = true; } Check(failed, reason); }
        string sandbox = Path.Combine(Path.GetTempPath(), "qol-workshop-test-" + Guid.NewGuid().ToString("N"));
        string beforeRoot = WorkshopStorage.Root, beforePresets = ToolPresetStore.DirectoryPath;
        WorkshopStorage.Root = sandbox; ToolPresetStore.DirectoryPath = Path.Combine(sandbox, "presets");
        const string design = "{\"header\":{\"name\":\"Test tank\"},\"objects\":[{\"vuid\":1}],\"meshes\":[],\"blueprints\":[]}";
        const string clip = "{\"version\":1,\"sourceVehicle\":\"Test tank\",\"copiedAt\":\"first\",\"objects\":[{\"vuid\":1}],\"blueprints\":[],\"meshes\":[]}";
        try
        {
            Check(WorkshopStorage.ListParts(false).Count == 0 && WorkshopStorage.ListBackups().Count == 0, "empty storage lists safely");
            var part = WorkshopStorage.SavePart("Exhaust / rear ..", clip, "engine, detail");
            Check(part.Count == 1 && WorkshopStorage.ReadPartJson(WorkshopStorage.ListParts(false).Single().Id, false) == clip, "part assembly and tags round trip");
            Check(WorkshopStorage.ListParts(false).Single().Name == "Exhaust / rear ..", "display names do not become paths");
            Check(!WorkshopStorage.DeletePart("../../outside", false), "delete rejects traversal");
            Throws(() => WorkshopStorage.SavePart("", clip), "empty names rejected");
            Throws(() => WorkshopStorage.SavePart("Invalid", "{\"objects\":[]}"), "empty groups rejected");
            Throws(() => WorkshopStorage.SavePart("Invalid", "not JSON"), "malformed groups rejected");
            var history = WorkshopStorage.SavePart("First copy", clip, history: true);
            var recopy = WorkshopStorage.SavePart("Copied again", clip.Replace("first", "second"), history: true);
            Check(history.Id == recopy.Id && WorkshopStorage.ListParts(true).Count == 1, "timestamp-only copies share history slot");
            for (int i = 0; i < 35; i++) WorkshopStorage.SavePart("Group " + i, clip.Replace("\"vuid\":1", "\"vuid\":" + (i + 10)), history: true);
            Check(WorkshopStorage.ListParts(true).Count == 30, "clipboard history bounded to 30 entries");
            Check(WorkshopStorage.ListParts(false).Count == 1, "history cleanup preserves named library");
            Check(WorkshopStorage.DeletePart(part.Id, false) && !WorkshopStorage.DeletePart(part.Id, false), "delete only selected saved group");
            // Review WF-02: text that escapes into far more bytes ('<' is written as <) fits as JSON but not as a file.
            long limit = WorkshopStorage.MaxPartBytes;
            try
            {
                WorkshopStorage.MaxPartBytes = 4000;
                string quoted = clip.Replace("\"first\"", "\"" + new string('<', 1500) + "\"");
                Throws(() => WorkshopStorage.SavePart("Escaped", quoted), "a group whose file would pass the limit is refused");
                Check(WorkshopStorage.ListParts(false).Count == 0 && !Directory.EnumerateFiles(WorkshopStorage.LibraryDirectory, "*", SearchOption.AllDirectories).Any(f => f.Contains("Saved")),
                    "a refused group leaves no file behind");
                var fits = WorkshopStorage.SavePart("Fits", clip);
                Check(WorkshopStorage.ListParts(false).Any(p => p.Id == fits.Id), "every group that saves can be listed straight after");
                WorkshopStorage.DeletePart(fits.Id, false);
                // Review WF-06: listing reads only the start of each file; a group too big to open is listed (to delete), not opened.
                var big = WorkshopStorage.SavePart("Big", clip.Replace("\"first\"", "\"" + new string('x', 3000) + "\""));
                WorkshopStorage.MaxPartBytes = 1000;
                var listed = WorkshopStorage.ListParts(false).Single(p => p.Id == big.Id);
                Check(listed.TooLarge && listed.Name == "Big" && listed.Count == 1, "a group over the limit is still listed, marked too large");
                Throws(() => WorkshopStorage.ReadPartJson(big.Id, false), "a group over the limit isn't opened");
                WorkshopStorage.MaxPartBytes = limit;
                Check(WorkshopStorage.ReadPartJson(big.Id, false).Contains(new string('x', 3000)), "the group itself is read only when asked for");
                string file = Path.Combine(WorkshopStorage.LibraryDirectory, "Saved", big.Id + ".json");
                File.WriteAllText(file, File.ReadAllText(file)[..200]); // cut off in its group: the list still shows it
                Check(WorkshopStorage.ListParts(false).Any(p => p.Id == big.Id), "a group cut off after its name is still listed");
                Throws(() => WorkshopStorage.ReadPartJson(big.Id, false), "and says it can't be read rather than opening half of it");
                WorkshopStorage.DeletePart(big.Id, false);
            }
            finally { WorkshopStorage.MaxPartBytes = limit; }

            var values = new Dictionary<string, JsonElement> { ["Fillet/radius"] = JsonSerializer.SerializeToElement(30f), ["Fillet/controls"] = JsonSerializer.SerializeToElement(true) };
            // Review WF-04: Create Hole's fill is an index into its fills (Fill's modes, then the game's fan); others reset to default.
            Check(Enumerable.Range(0, Fill.ModeNames.Length + 1).All(i => ToolSettingRanges.Valid("holeFill", i)), "every hole fill can be kept");
            Check(new[] { -1, Fill.ModeNames.Length + 1, 1000 }.All(i => !ToolSettingRanges.Valid("holeFill", i)), "a hole fill past the list is refused (default kept)");
            var preset = ToolPresetStore.Save("Fillet", "Small / round", values);
            Check(ToolPresetStore.List("Fillet").Single().Values["Fillet/radius"].GetSingle() == 30, "typed preset values persist");
            values["Fillet/radius"] = JsonSerializer.SerializeToElement(15f);
            var updated = ToolPresetStore.Save("Fillet", "Small / round", values);
            Check(preset.Id == updated.Id && ToolPresetStore.List().Count == 1 && ToolPresetStore.List().Single().Values["Fillet/radius"].GetSingle() == 15, "same group and name replaces a preset");
            ToolPresetStore.Save("Hole", "Small / round", values);
            Check(ToolPresetStore.List().Count == 2 && ToolPresetStore.List("Hole").Count == 1, "same names in different groups remain independent");
            Check(!ToolPresetStore.Delete("../bad") && ToolPresetStore.Delete(preset.Id), "preset deletion guards paths");
            Throws(() => ToolPresetStore.Save("Group", "", values), "empty preset names rejected");
            Throws(() => ToolPresetStore.Save("Group", "Empty", new Dictionary<string, JsonElement>()), "empty presets rejected");
            Directory.CreateDirectory(ToolPresetStore.DirectoryPath);
            File.WriteAllText(Path.Combine(ToolPresetStore.DirectoryPath, "broken.json"), "{");
            Check(ToolPresetStore.List().Count == 1, "damaged preset does not hide healthy presets");

            var checkpoint = WorkshopStorage.SaveCheckpoint("Before turret rebuild", design);
            Check(checkpoint.Pinned && WorkshopStorage.ListBackups().Single().Name == "Before turret rebuild", "named checkpoint pinned by default");
            string faction = Path.Combine(sandbox, "Faction");
            string restored = WorkshopStorage.RestoreCopy(checkpoint.Id, faction);
            Check(File.Exists(restored) && JsonNode.Parse(File.ReadAllText(restored))!["header"]!["name"]!.GetValue<string>() == "Test tank recovery", "recovery copy has distinct internal name");
            string second = WorkshopStorage.RestoreCopy(checkpoint.Id, faction);
            Check(restored != second && File.ReadAllText(restored).Contains("Test tank recovery") && File.ReadAllText(Path.Combine(checkpoint.Directory, "original.blueprint")) == design, "second recovery does not overwrite source or prior copy");
            Throws(() => WorkshopStorage.RestoreCopy("../elsewhere", faction), "recovery rejects unknown IDs");
            Throws(() => WorkshopStorage.SaveCheckpoint("Invalid", clip), "clipboard cannot masquerade as complete checkpoint");
            for (int i = 0; i < 4; i++)
            {
                string folder = Path.Combine(WorkshopStorage.BackupDirectory, $"20240101-00000{i}-12345678");
                Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "original.blueprint"), design);
            }
            string unrelated = Path.Combine(WorkshopStorage.BackupDirectory, "user-important-stuff"); Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "preserve");
            WorkshopStorage.Prune(2);
            Check(WorkshopStorage.ListBackups().Count == 3 && Directory.Exists(checkpoint.Directory), "pruning retains pinned checkpoint plus newest unpinned backups");
            Check(File.Exists(Path.Combine(unrelated, "keep.txt")), "pruning leaves unrelated folders intact");
            WorkshopStorage.SetPinned(checkpoint.Id, false);
            Check(!WorkshopStorage.IsPinned(checkpoint.Directory), "unpin persists");
            WorkshopStorage.SetPinned(checkpoint.Id, true);
            Check(WorkshopStorage.IsPinned(checkpoint.Directory), "pin persists");
            File.WriteAllText(Path.Combine(checkpoint.Directory, "workshop.json"), "broken JSON");
            Check(WorkshopStorage.IsPinned(checkpoint.Directory) && WorkshopStorage.ListBackups().Any(b => b.Id == checkpoint.Id && b.Pinned), "damaged pin metadata preserves and displays checkpoint");
            WorkshopStorage.Prune(1);
            Check(Directory.Exists(checkpoint.Directory) && WorkshopStorage.ListBackups().Count == 2, "prune preserves unreadable pin metadata");
            // Review WF-01: notes that read as JSON but aren't whole must protect the checkpoint too.
            foreach (string damaged in new[] { "{}", "null", "[]", "{\"Name\":\"x\",\"Operation\":\"y\",\"CreatedUtc\":\"2024-01-01T00:00:00Z\"}",
                         "{\"Name\":\"x\",\"Operation\":\"y\",\"CreatedUtc\":\"2024-01-01T00:00:00Z\",\"Pinned\":\"yes\"}", "{\"Pinned\":false}", "{\"Name\":\"x\",\"Pin" })
            {
                File.WriteAllText(Path.Combine(checkpoint.Directory, "workshop.json"), damaged);
                Check(WorkshopStorage.IsPinned(checkpoint.Directory) && WorkshopStorage.ListBackups().Any(b => b.Id == checkpoint.Id && b.Pinned), $"note {damaged} keeps the checkpoint pinned");
                WorkshopStorage.Prune(1);
                Check(Directory.Exists(checkpoint.Directory), $"note {damaged} keeps the checkpoint through pruning");
            }
            WorkshopStorage.SetPinned(checkpoint.Id, false);
            Check(!WorkshopStorage.IsPinned(checkpoint.Directory), "a whole note can still unpin it");
            WorkshopStorage.SetPinned(checkpoint.Id, true);
            Throws(() => WorkshopStorage.IsPinned(sandbox), "pin lookup cannot leave backup root");
            string report = WorkshopStorage.SaveSupportReport("Versions and hooks only");
            Check(File.ReadAllText(report) == "Versions and hooks only" && report.StartsWith(Path.Combine(sandbox, "SprocketQoL", "Reports")), "support reports isolated from designs");
            Check(!Directory.EnumerateFiles(sandbox, "*.tmp", SearchOption.AllDirectories).Any(), "atomic writes leave no temporary files");
        }
        finally
        {
            WorkshopStorage.Root = beforeRoot; ToolPresetStore.DirectoryPath = beforePresets;
            string absolute = Path.GetFullPath(sandbox), expected = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("qol-workshop-test-", StringComparison.Ordinal) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
        string leaky = @"Image: 'file:///C:/Users/Some%20One/Documents/My%20Games/Sprocket/Decals/a.png' and C:\Users\Some One\Desktop\b.txt and c:/users/some one/c.txt";
        string clean = WorkshopStorage.Scrub(leaky, (@"C:\Users\Some One", "[User]"), ("ab", "[too short]"));
        Check(!clean.Contains("Some", StringComparison.OrdinalIgnoreCase) && clean.Split("[User]").Length == 4, "support report hides the user folder in every form: backslashes, file:/// with %20, any case");
        Check(WorkshopStorage.Scrub(@"D:\Games\Sprocket\BepInEx\x", (@"D:\Games\Sprocket", "[Game]"), (@"D:\Games\Sprocket\BepInEx", "[BepInEx]")) == @"[BepInEx]\x", "the longer path is replaced first");
        Console.WriteLine($"WORKSHOP_STORAGE_TESTS_OK {checks}");
    }
}
