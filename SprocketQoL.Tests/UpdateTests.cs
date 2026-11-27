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
            File.WriteAllText(Path.Combine(plugins, "SprocketQoL.dll.old"), "user backup");
            File.WriteAllText(Path.Combine(plugins, "SprocketQoL-other.dll.old"), "other user backup");
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
            Check(File.ReadAllText(Path.Combine(plugins, "SprocketQoL.dll")) == "new dll" && File.ReadAllText(Path.Combine(plugins, "SprocketQoL.dll.qol-update-old")) == "old dll",
                "the running DLL is moved aside and the new one put in its place");
            Check(File.ReadAllText(Path.Combine(plugins, "SprocketQoL-README.txt")) == "new readme" && !File.Exists(Path.Combine(plugins, "SprocketQoL.pdb")),
                "files already there are replaced; none are added");
            Check(File.ReadAllText(Path.Combine(manager, "SprocketQoL.dll")) == "new dll" && File.ReadAllText(Path.Combine(manager, "SprocketQoL-LICENSE.txt")) == "new licence"
                  && !File.Exists(Path.Combine(manager, "SprocketQoL-README.txt")), "the Mod Manager's copy is updated the same way");
            Check(File.ReadAllText(Path.Combine(plugins, "SprocketQoL.dll.old")) == "user backup"
                  && File.ReadAllText(Path.Combine(plugins, "SprocketQoL-other.dll.old")) == "other user backup"
                  && UpdateRules.OldUpdateFiles(plugins).Single() == Path.Combine(plugins, "SprocketQoL.dll.qol-update-old"),
                "only explicitly marked QoL update backups are eligible for cleanup");
            File.Move(Path.Combine(plugins, "SprocketQoL.dll"), Path.Combine(plugins, "SprocketQoL.dll.qol-update-old2"));
            Check(!UpdateRules.OldUpdateFiles(plugins).Any(path => path.EndsWith("SprocketQoL.dll.qol-update-old2", StringComparison.Ordinal)),
                "an updater backup is retained when its destination is missing");
            File.Move(Path.Combine(plugins, "SprocketQoL.dll.qol-update-old2"), Path.Combine(plugins, "SprocketQoL.dll"));
            Check(File.ReadAllText(Path.Combine(other, "SprocketQoL.dll")) == "other game's dll", "another game's copy is left alone");
            Check(!Directory.EnumerateFiles(sandbox, "evil.txt", SearchOption.AllDirectories).Any() && !File.Exists(Path.Combine(sandbox, "..", "evil.txt"))
                  && !Directory.EnumerateFiles(sandbox, "*.new", SearchOption.AllDirectories).Any(), "nothing but the release's own files is written, and no temporary files stay");

            string transactionPlugin = Path.Combine(sandbox, "transaction", "plugins");
            string transactionManager = Path.Combine(sandbox, "transaction", "manager", "plugins");
            Directory.CreateDirectory(transactionPlugin); Directory.CreateDirectory(transactionManager);
            File.WriteAllText(Path.Combine(transactionPlugin, UpdateRules.Dll), "plugin before");
            File.WriteAllText(Path.Combine(transactionPlugin, "SprocketQoL-README.txt"), "plugin guide before");
            File.WriteAllText(Path.Combine(transactionManager, UpdateRules.Dll), "manager before");
            File.WriteAllText(Path.Combine(transactionManager, "SprocketQoL-LICENSE.txt"), "manager licence before");
            string managerDll = Path.Combine(transactionManager, UpdateRules.Dll);
            void FailManagerInstall(string source, string destination, bool overwrite)
            {
                if (destination == managerDll && source.EndsWith(".new", StringComparison.Ordinal))
                    throw new IOException("simulated manager copy failure");
                File.Move(source, destination, overwrite);
            }
            Throws(() => UpdateRules.Install(bytes, sha, transactionPlugin, new[] { managerDll }, FailManagerInstall),
                "a failed Mod Manager copy reports the transaction failure");
            Check(File.ReadAllText(Path.Combine(transactionPlugin, UpdateRules.Dll)) == "plugin before"
                  && File.ReadAllText(Path.Combine(transactionPlugin, "SprocketQoL-README.txt")) == "plugin guide before"
                  && File.ReadAllText(managerDll) == "manager before"
                  && File.ReadAllText(Path.Combine(transactionManager, "SprocketQoL-LICENSE.txt")) == "manager licence before"
                  && !Directory.EnumerateFiles(Path.Combine(sandbox, "transaction"), "*.new", SearchOption.AllDirectories).Any(),
                "a later destination failure restores all earlier plugin files and the locked manager DLL");

            string rollback = Path.Combine(sandbox, "rollback.dll");
            File.WriteAllText(rollback, "previous");
            bool failSecondMove = true;
            void FailInstallAfterAside(string source, string destination, bool overwrite)
            {
                if (failSecondMove && destination == rollback && source.EndsWith(".new", StringComparison.Ordinal) && !File.Exists(rollback))
                { failSecondMove = false; throw new IOException("simulated install failure after aside"); }
                if (destination == rollback && source.EndsWith(".new", StringComparison.Ordinal) && File.Exists(rollback))
                { throw new IOException("simulated locked replacement"); }
                File.Move(source, destination, overwrite);
            }
            Throws(() => UpdateRules.Put(rollback, "replacement"u8.ToArray(), FailInstallAfterAside), "failed replacement propagates");
            Check(File.ReadAllText(rollback) == "previous"
                  && !Directory.EnumerateFiles(sandbox, "rollback.dll.qol-update-old*", SearchOption.AllDirectories).Any()
                  && !File.Exists(rollback + ".new"), "failed install after moving the old DLL restores it and cleans its temp file");
        }
        finally { Directory.Delete(sandbox, true); }

        // The updater compares Plugin.Version with the newest release. 1.11.1 shipped with the project at 1.11.1 but
        // Plugin.Version still 1.11.0, so every start "updated" 1.11.0 -> 1.11.1 again: the two must be the same.
        static DirectoryInfo? Above(string start)
        {
            var d = new DirectoryInfo(start);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "SprocketMod.props"))) d = d.Parent;
            return d;
        }
        if ((Above(AppContext.BaseDirectory) ?? Above(Environment.CurrentDirectory)) is not { } root)
            Console.WriteLine("VERSION_CHECK_SKIPPED: the QoL sources aren't above the test program or the folder it runs in");
        else
        {
            var plugin = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(root.FullName, "SprocketQoL", "Plugin.cs")), @"const string Version = ""([^""]+)""").Groups[1].Value;
            var project = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(root.FullName, "SprocketQoL", "SprocketQoL.csproj")), @"<Version>([^<]+)</Version>").Groups[1].Value;
            Check(plugin.Length > 0 && plugin == project, $"Plugin.Version ({plugin}) is the project's version ({project}), or the updater reinstalls the same release every start");
        }
        Console.WriteLine($"UPDATE_TESTS_OK: {checks} checks");
    }
}
