using System.Net.Http;

namespace SprocketQoL;

/// Quality of Life updates itself (on by default; Mod Options > Compatibility): at each game start it looks for a newer
/// release on GitHub for this game version (UpdateRules), checks the download against the release's SHA-256 and puts its
/// files in place of the running ones. Windows lets a loaded DLL be renamed but not replaced, so the running one is moved
/// aside (deleted at the next start) and the new version loads when the game starts again. The Mod Manager's copy is
/// updated as well: the manager puts its copy back whenever the mod is turned on, which would undo the update.
internal static class Updater
{
    internal static string Status { get; private set; } = "Not checked yet.";
    /// A finished update's message, shown once in the editor (DesignEditor.Update).
    internal static volatile string? Pending;
    static int running;
    static string pluginDir = "", gameRoot = "", current = "", game = "";

    /// From Plugin.Load, on the main thread (the game's version is read there).
    internal static void Start(string dllPath, string root, string version, string gameVersion)
    {
        pluginDir = Path.GetDirectoryName(dllPath) ?? ""; gameRoot = root; current = version; game = gameVersion;
        try { foreach (var old in Directory.EnumerateFiles(pluginDir, "SprocketQoL*.old*")) try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"QOL_UPDATE couldn't tidy the last update's old files: {ex.Message}"); }
        if (Plugin.AutoUpdate?.Value == true) Check(); else Status = "Automatic updates are off.";
    }

    internal static void Check()
    {
        if (pluginDir.Length == 0 || Interlocked.Exchange(ref running, 1) == 1) return;
        Status = "Checking for updates...";
        Task.Run(async () =>
        {
            try { Status = await Run(); }
            catch (Exception ex) { Status = "Update check failed: " + ex.Message; Plugin.ModLog.LogWarning("QOL_UPDATE " + Status); }
            finally { Interlocked.Exchange(ref running, 0); }
        });
    }

    static async Task<string> Run()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SprocketQoL-updater/" + current); // GitHub's API refuses requests without one
        var release = UpdateRules.Newest(await http.GetStringAsync($"https://api.github.com/repos/{UpdateRules.Repo}/releases?per_page=30"), game);
        if (release == null || UpdateRules.Compare(release.Version, current) <= 0)
        {
            Plugin.ModLog.LogInfo($"QOL_UPDATE up to date: {current} (newest for game {game}: {release?.Version ?? "none"})");
            return $"Up to date ({current}).";
        }
        string sha = UpdateRules.Sha(await http.GetStringAsync(release.ShaUrl)) ?? throw new InvalidDataException("the release's .sha256 file can't be read");
        byte[] zip = await http.GetByteArrayAsync(release.ZipUrl);
        var copies = UpdateRules.ManagerCopies(gameRoot);
        UpdateRules.Install(zip, sha, pluginDir, copies);
        string done = $"Quality of Life {release.Version} is installed: restart Sprocket to use it.";
        Plugin.ModLog.LogInfo($"QOL_UPDATE {current} -> {release.Version} ({release.Zip}, SHA-256 checked) into {pluginDir}" + (copies.Count > 0 ? $" and {copies.Count} Mod Manager cop{(copies.Count == 1 ? "y" : "ies")}" : ""));
        Pending = done;
        return done;
    }
}
