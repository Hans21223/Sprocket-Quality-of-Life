using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using SprocketQoL;

/// The self-updater's rules (UpdateRules): versions, game versions, which release, and the files it writes.
static class UpdateTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string why) { checks++; if (!ok) throw new Exception("Update: " + why); }
        void Throws(Action action, string why) { bool threw = false; try { action(); } catch { threw = true; } Check(threw, why); }

        Check(UpdateRules.Compare("1.11.0", "1.10.8") > 0 && UpdateRules.Compare("1.10.10", "1.10.9") > 0 && UpdateRules.Compare("1.8.8", "1.8.8.0") == 0,
            "versions compare by their numbers");
        JsonNode Spec(string s) => JsonValue.Create(s)!;
        Check(UpdateRules.Fits(Spec(">=0.2.55.5"), "0.2.56.2") && !UpdateRules.Fits(Spec(">=0.2.55.5"), "0.2.55.4"), ">= game versions");
        Check(UpdateRules.Fits(Spec("0.2.56"), "0.2.56.2") && !UpdateRules.Fits(Spec("0.2.56"), "0.2.55.5"), "a bare version matches the parts it states");
        Check(UpdateRules.Fits(JsonNode.Parse("[\"0.2.55\", \">=0.2.57\"]"), "0.2.55.5") && !UpdateRules.Fits(JsonNode.Parse("[\"0.2.55\", \">=0.2.57\"]"), "0.2.56.2"),
            "a list fits when one of its entries does");
        Check(UpdateRules.Fits(null, "0.2.56.2") && UpdateRules.Fits(Spec(">=9"), ""), "no stated versions, or an unreadable game version: it fits");
        Check(UpdateRules.GameSpec("Notes\n\n<!-- sp-compat {\"hamish.sprocket\": \">=0.2.55.5\", \"bepinex.bepinex\": \"6.0.0-be.788\"} -->")?.GetValue<string>() == ">=0.2.55.5"
              && UpdateRules.GameSpec("no comment") == null, "the release's sp-compat comment is read");

        string Asset(string name, string repo = UpdateRules.Repo) => $"{{\"name\":\"{name}\",\"browser_download_url\":\"https://github.com/{repo}/releases/download/v/{name}\"}}";
        string ReleaseJson(string v, string extra = "", string body = "", string? repo = null) =>
            $"{{\"tag_name\":\"v{v}\",{extra}\"body\":\"{body}\",\"assets\":[{Asset($"Quality-of-Life-{v}.zip", repo ?? UpdateRules.Repo)},{Asset($"Quality-of-Life-{v}.zip.sha256", repo ?? UpdateRules.Repo)}]}}";
        string list = "[" + string.Join(",",
            ReleaseJson("1.11.9", "\"draft\":true,"), ReleaseJson("1.11.8", "\"prerelease\":true,"),
            ReleaseJson("1.11.7", body: "<!-- sp-compat {\\\"hamish.sprocket\\\": \\\">=0.3\\\"} -->"),
            ReleaseJson("1.11.6", repo: "someone/else"), ReleaseJson("1.11.2"), ReleaseJson("1.11.1")) + "]";
        var newest = UpdateRules.Newest(list, "0.2.56.2");
        Check(newest?.Version == "1.11.2" && newest.ZipUrl.EndsWith("/Quality-of-Life-1.11.2.zip") && newest.ShaUrl.EndsWith(".zip.sha256"),
            $"the newest release for this game: not a draft, pre-release, other game or other repository's file ({newest?.Version})");
        Check(UpdateRules.Newest(list, "0.3.0")?.Version == "1.11.7", "a release for a newer game is taken on that game");
        Check(UpdateRules.Sha("ABCDEF0123456789abcdef0123456789ABCDEF0123456789abcdef0123456789  Quality-of-Life-1.11.2.zip\r\n") == "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
              && UpdateRules.Sha("not a hash") == null, "the .sha256 file's hash is read");

        string sandbox = Path.Combine(Path.GetTempPath(), "qol-update-" + Guid.NewGuid().ToString("N"));
        try
        {
            string plugins = Path.Combine(sandbox, "BepInEx", "plugins");
            string manager = Path.Combine(sandbox, "ModManager", "mods", "Sprocket", "Quality of Life", "BepInEx", "plugins");
            string other = Path.Combine(sandbox, "ModManager", "mods", "Other game", "Quality of Life", "BepInEx", "plugins");
            foreach (var d in new[] { plugins, manager, other }) Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(plugins, "SprocketQoL.dll"), "old dll");
            File.WriteAllText(Path.Combine(plugins, "SprocketQoL-README.txt"), "old readme");
            File.WriteAllText(Path.Combine(manager, "SprocketQoL.dll"), "old dll");
            File.WriteAllText(Path.Combine(manager, "SprocketQoL-LICENSE.txt"), "old licence");
            File.WriteAllText(Path.Combine(other, "SprocketQoL.dll"), "other game's dll");
            File.WriteAllText(Path.Combine(sandbox, "ModManager", "games.json"),
                new JsonObject { ["Sprocket"] = new JsonObject { ["dir"] = sandbox + Path.DirectorySeparatorChar }, ["Other game"] = new JsonObject { ["dir"] = Path.Combine(sandbox, "elsewhere") } }.ToJsonString());
            var copies = UpdateRules.ManagerCopies(sandbox);
            Check(copies.Count == 1 && copies[0] == Path.Combine(manager, "SprocketQoL.dll"), "only this game's Mod Manager copy is found");

            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (name, text) in new[] { ("BepInEx/plugins/SprocketQoL.dll", "new dll"), ("BepInEx/plugins/SprocketQoL.pdb", "new pdb"),
                             ("BepInEx/plugins/SprocketQoL-README.txt", "new readme"), ("BepInEx/plugins/SprocketQoL-LICENSE.txt", "new licence"), ("../../evil.txt", "no") })
                {
                    using var w = new StreamWriter(zip.CreateEntry(name).Open());
                    w.Write(text);
                }
            byte[] bytes = stream.ToArray();
            string sha = Convert.ToHexString(SHA256.HashData(bytes));
            Throws(() => UpdateRules.Install(bytes, new string('0', 64), plugins, copies), "a download that doesn't match the SHA-256 is refused");
            Check(File.ReadAllText(Path.Combine(plugins, "SprocketQoL.dll")) == "old dll", "a refused download changes nothing");
            // The running DLL is open the way a loaded assembly is: it can be renamed, not replaced.
            using (new FileStream(Path.Combine(plugins, "SprocketQoL.dll"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                UpdateRules.Install(bytes, sha, plugins, copies);
            Check(File.ReadAllText(Path.Combine(plugins, "SprocketQoL.dll")) == "new dll" && File.ReadAllText(Path.Combine(plugins, "SprocketQoL.dll.old")) == "old dll",
                "the running DLL is moved aside and the new one put in its place");
            Check(File.ReadAllText(Path.Combine(plugins, "SprocketQoL-README.txt")) == "new readme" && !File.Exists(Path.Combine(plugins, "SprocketQoL.pdb")),
                "files already there are replaced; none are added");
            Check(File.ReadAllText(Path.Combine(manager, "SprocketQoL.dll")) == "new dll" && File.ReadAllText(Path.Combine(manager, "SprocketQoL-LICENSE.txt")) == "new licence"
                  && !File.Exists(Path.Combine(manager, "SprocketQoL-README.txt")), "the Mod Manager's copy is updated the same way");
            Check(File.ReadAllText(Path.Combine(other, "SprocketQoL.dll")) == "other game's dll", "another game's copy is left alone");
            Check(!Directory.EnumerateFiles(sandbox, "evil.txt", SearchOption.AllDirectories).Any() && !File.Exists(Path.Combine(sandbox, "..", "evil.txt"))
                  && !Directory.EnumerateFiles(sandbox, "*.new", SearchOption.AllDirectories).Any(), "nothing but the release's own files is written, and no temporary files stay");
        }
        finally { Directory.Delete(sandbox, true); }
        Console.WriteLine($"UPDATE_TESTS_OK: {checks} checks");
    }
}
