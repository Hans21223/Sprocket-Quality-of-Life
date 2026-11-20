using System.Text;
using System.Text.Json.Nodes;
using SprocketQoL;

static class FactionRenameTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string reason) { checks++; if (!value) throw new Exception("Faction rename: " + reason); }
        void Throws(Action action, string reason) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, reason); }
        string sandbox = Path.Combine(Path.GetTempPath(), "qol-faction-rename-" + Guid.NewGuid().ToString("N"));
        byte[] definition = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{ \"name\":\"Original\", \"prefix\":\"ZZ\", \"count\":42, \"unknown\":{\"keep\":true} }\r\n")).ToArray();
        string Make(string name)
        {
            string folder = Path.Combine(sandbox, name);
            Directory.CreateDirectory(Path.Combine(folder, "Blueprints", "Vehicles"));
            File.WriteAllBytes(Path.Combine(folder, name + ".fdef"), definition);
            File.WriteAllText(Path.Combine(folder, "Blueprints", "Vehicles", "Exact file.blueprint"), "vehicle contents");
            return folder;
        }
        try
        {
            string original = Make("Original");
            string activated = "";
            string renamed = WorkshopStorage.RenameFaction(original, "New faction", path =>
            {
                Check(File.Exists(Path.Combine(path, "New faction.fdef")), "activation sees a fully written definition");
                activated = path;
            });
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(renamed, "New faction.fdef")))!;
            Check(!Directory.Exists(original) && activated == renamed && Directory.Exists(renamed), "folder and active binding move together");
            Check(node["name"]!.GetValue<string>() == "New faction" && node["prefix"]!.GetValue<string>() == "ZZ"
                && node["count"]!.GetValue<int>() == 42 && node["unknown"]!["keep"]!.GetValue<bool>(), "all unknown definition fields survive rename");
            Check(!File.Exists(Path.Combine(renamed, "Original.fdef"))
                && File.ReadAllText(Path.Combine(renamed, "Blueprints", "Vehicles", "Exact file.blueprint")) == "vehicle contents", "only the definition name changes");

            string caseSource = Make("CaseName");
            string caseResult = WorkshopStorage.RenameFaction(caseSource, "CASENAME");
            Check(Path.GetFileName(caseResult) == "CASENAME" && File.Exists(Path.Combine(caseResult, "CASENAME.fdef")), "case-only rename has a usable definition");
            Check(!Directory.EnumerateDirectories(sandbox).Any(d => d.Contains(".renaming-", StringComparison.Ordinal)), "case-only rename leaves no spare folder");

            string rollback = Make("Rollback"); bool restoreCalled = false;
            Throws(() => WorkshopStorage.RenameFaction(rollback, "Cannot activate", _ => throw new IOException("native reload refused"), () => restoreCalled = true),
                "failed active-faction reload refuses the rename");
            Check(restoreCalled && Directory.Exists(rollback) && !Directory.Exists(Path.Combine(sandbox, "Cannot activate")), "failed activation restores original folder and binding");
            Check(File.ReadAllBytes(Path.Combine(rollback, "Rollback.fdef")).SequenceEqual(definition)
                && !File.Exists(Path.Combine(rollback, "Cannot activate.fdef")), "failed activation restores original definition bytes and removes new definition");

            string caseRollback = Make("CaseRollback");
            Throws(() => WorkshopStorage.RenameFaction(caseRollback, "CASEROLLBACK", _ => throw new IOException("refused")), "case-only activation failure is refused");
            Check(Directory.Exists(caseRollback) && File.ReadAllBytes(Path.Combine(caseRollback, "CaseRollback.fdef")).SequenceEqual(definition),
                "case-only activation failure restores original folder and exact definition");

            string leg = Make("TwoLeg"); int moves = 0;
            Throws(() => WorkshopStorage.RenameFactionCore(leg, "TWOLEG", null, null, (from, to) =>
            {
                if (++moves == 2) throw new IOException("second move failed");
                Directory.Move(from, to);
            }), "second case-only move failure is surfaced");
            Check(moves == 2 && Directory.Exists(leg) && File.ReadAllBytes(Path.Combine(leg, "TwoLeg.fdef")).SequenceEqual(definition),
                "failed second leg returns the spare folder to the original name");
            Check(!Directory.EnumerateDirectories(sandbox).Any(d => d.Contains(".renaming-", StringComparison.Ordinal)), "failed second leg strands no temporary folder");

            string occupied = Make("Occupied");
            Throws(() => WorkshopStorage.RenameFaction(rollback, "Occupied"), "an existing faction cannot be overwritten");
            Check(File.ReadAllBytes(Path.Combine(occupied, "Occupied.fdef")).SequenceEqual(definition), "refused rename preserves other faction");
            foreach (string invalid in new[] { ".", "..", "NUL", "CON.txt", "COM1", "LPT9", "a/b", new string('x', 61) })
                Throws(() => WorkshopStorage.RenameFaction(rollback, invalid), "invalid folder name refused: " + invalid);
            File.WriteAllText(Path.Combine(rollback, "Extra.fdef"), "keep extra");
            Throws(() => WorkshopStorage.RenameFaction(rollback, "Extra"), "an existing second definition cannot be overwritten");
            Check(File.ReadAllText(Path.Combine(rollback, "Extra.fdef")) == "keep extra", "existing second definition is preserved");
            string malformed = Make("Malformed"); File.WriteAllText(Path.Combine(malformed, "Malformed.fdef"), "{");
            Throws(() => WorkshopStorage.RenameFaction(malformed, "Bad result"), "malformed definition is refused before moving");
            Check(Directory.Exists(malformed) && File.ReadAllText(Path.Combine(malformed, "Malformed.fdef")) == "{", "malformed faction stays in place");
        }
        finally
        {
            string absolute = Path.GetFullPath(sandbox), temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(absolute).StartsWith("qol-faction-rename-", StringComparison.Ordinal) && Directory.Exists(absolute)) Directory.Delete(absolute, true);
        }
        Console.WriteLine($"FACTION_RENAME_TESTS_OK: {checks} checks");
    }
}
