using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SprocketQoL;

/// What the updater (Updater) decides, apart from the network so it's tested offline. The rules are the Mod Manager's
/// (ModManager\updates.py), so both pick the same release: versions compare by their numbers, and a release says which
/// game versions it's for in its "sp-compat" comment.
public static class UpdateRules
{
    public const string Repo = "Hans21223/Sprocket-Quality-of-Life";
    public const string Dll = "SprocketQoL.dll";
    /// The only place a download is taken from: this repository's own release files.
    public const string DownloadPrefix = "https://github.com/" + Repo + "/releases/download/";
    /// The files a release zip holds, all in BepInEx/plugins; nothing else in the zip is ever written.
    public static readonly string[] Files = { Dll, "SprocketQoL.pdb", "SprocketQoL-README.txt", "SprocketQoL-LICENSE.txt" };

    public static int[] Version(string? text)
    {
        var parts = Regex.Matches(text ?? "", @"\d+").Take(4).Select(m => int.TryParse(m.Value, out int v) ? v : int.MaxValue).ToList();
        while (parts.Count < 4) parts.Add(0); // 1.8.8 == 1.8.8.0
        return parts.ToArray();
    }

    public static int Compare(string? a, string? b)
    {
        var x = Version(a); var y = Version(b);
        for (int i = 0; i < 4; i++) if (x[i] != y[i]) return x[i].CompareTo(y[i]);
        return 0;
    }

    /// A release's game versions (the "hamish.sprocket" of its sp-compat comment), or null without one.
    public static JsonNode? GameSpec(string? body)
    {
        var found = Regex.Match(body ?? "", @"<!--\s*sp-compat\s+(\{.*?\})\s*-->", RegexOptions.Singleline);
        if (!found.Success) return null;
        try { return JsonNode.Parse(found.Groups[1].Value)?["hamish.sprocket"]; } catch (System.Text.Json.JsonException) { return null; }
    }

    /// Whether a game version fits an sp-compat value: "0.2.56" (those parts), ">=0.2.55.5", or a list of them.
    /// Unstated, or a game version that can't be read: it fits.
    public static bool Fits(JsonNode? spec, string? game)
    {
        if (spec == null || string.IsNullOrWhiteSpace(game)) return true;
        if (spec is JsonArray list) return list.Any(s => Fits(s, game));
        string text = spec is JsonValue v && v.TryGetValue<string>(out var s2) ? s2 : spec.ToJsonString();
        var m = Regex.Match(text, @"^\s*(>=|<=|==|>|<)?\s*(.*)$");
        string op = m.Groups[1].Value, wanted = m.Groups[2].Value;
        int c = Compare(game, wanted);
        if (op.Length == 0)
        {
            int stated = Regex.Matches(wanted, @"\d+").Count;
            return Version(game).Take(stated).SequenceEqual(Version(wanted).Take(stated));
        }
        return op switch { ">=" => c >= 0, "<=" => c <= 0, "==" => c == 0, ">" => c > 0, _ => c < 0 };
    }

    public sealed record Release(string Version, string Zip, string ZipUrl, string ShaUrl);

    /// The newest release for this game in the GitHub API's release list: its QoL zip and that zip's .sha256, both from
    /// this repository's releases. Drafts and pre-releases (not tried in the game yet) are left out.
    public static Release? Newest(string releasesJson, string? game)
    {
        Release? best = null;
        if (JsonNode.Parse(releasesJson) is not JsonArray releases) return null;
        foreach (var r in releases.OfType<JsonObject>())
        {
            if (r["draft"]?.GetValue<bool>() == true || r["prerelease"]?.GetValue<bool>() == true || !Fits(GameSpec(r["body"]?.GetValue<string>()), game)) continue;
            var assets = (r["assets"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(a => (Name: a["name"]?.GetValue<string>() ?? "", Url: a["browser_download_url"]?.GetValue<string>() ?? ""))
                .Where(a => a.Url.StartsWith(DownloadPrefix, StringComparison.Ordinal)).GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First().Url);
            foreach (var (name, url) in assets)
                if (Regex.Match(name, @"^Quality-of-Life-(\d+(?:\.\d+){1,3})\.zip$") is { Success: true } m && assets.TryGetValue(name + ".sha256", out var sha)
                    && (best == null || Compare(m.Groups[1].Value, best.Version) > 0))
                    best = new(m.Groups[1].Value, name, url, sha);
        }
        return best;
    }

    /// The SHA-256 a release's .sha256 file names (its first word, as `sha256sum` writes it), or null.
    public static string? Sha(string shaFile) => Regex.Match(shaFile ?? "", @"^\s*([0-9a-fA-F]{64})\b") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// The Mod Manager's copies of this mod for the game in `gameRoot`: ModManager\games.json names each game it manages
    /// and its folder, and that game's mods sit in ModManager\mods\(game)\(mod)\...; a copy is the DLL found there. The
    /// manager puts its copy into the game whenever the mod is turned on, so it's updated too, or it would undo this.
    public static List<string> ManagerCopies(string gameRoot)
    {
        var copies = new List<string>();
        string manager = Path.Combine(gameRoot, "ModManager"), games = Path.Combine(manager, "games.json");
        if (!File.Exists(games)) return copies;
        if (JsonNode.Parse(File.ReadAllText(games)) is not JsonObject all) return copies;
        string root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var (name, game) in all)
        {
            string? dir = game?["dir"]?.GetValue<string>();
            if (dir == null || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..") continue;
            if (!string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) continue;
            string mods = Path.Combine(manager, "mods", name);
            if (Directory.Exists(mods)) copies.AddRange(Directory.EnumerateFiles(mods, Dll, SearchOption.AllDirectories));
        }
        return copies;
    }

    /// A release zip's files put into `pluginDir` and each Mod Manager copy's folder, after checking the zip against
    /// `sha`. Only the release's own files are taken (BepInEx/plugins/..., by exact name), and only files already in a
    /// folder are replaced, besides the DLL itself, so a folder never gets files its owner doesn't know about.
    internal static void Install(byte[] zip, string sha, string pluginDir, IEnumerable<string> managerCopies, Action<string, string, bool>? moveFile = null)
    {
        if (!Convert.ToHexString(SHA256.HashData(zip)).Equals(sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("the download doesn't match the release's SHA-256");
        var files = new Dictionary<string, byte[]>();
        using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            foreach (var name in UpdateRules.Files)
                if (archive.GetEntry("BepInEx/plugins/" + name) is { } entry)
                {
                    using var from = entry.Open();
                    using var to = new MemoryStream();
                    from.CopyTo(to);
                    files[name] = to.ToArray();
                }
        if (!files.ContainsKey(UpdateRules.Dll)) throw new InvalidDataException($"the release has no {UpdateRules.Dll}");
        // Stage every old value before the first write. If one manager copy is locked or unwritable,
        // the game's plugin and earlier manager copies are restored instead of diverging.
        var writes = new List<(string Path, byte[] New, byte[]? Old)>();
        foreach (var dir in managerCopies.Select(Path.GetDirectoryName).OfType<string>().Prepend(pluginDir).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var (name, bytes) in files)
            {
                string path = Path.Combine(dir, name);
                if (name != UpdateRules.Dll && !File.Exists(path)) continue;
                writes.Add((path, bytes, File.Exists(path) ? File.ReadAllBytes(path) : null));
            }

        var installed = new List<(string Path, byte[]? Old)>();
        try
        {
            foreach (var write in writes)
            {
                Put(write.Path, write.New, moveFile);
                installed.Add((write.Path, write.Old));
            }
        }
        catch (Exception installError)
        {
            var errors = new List<Exception> { installError };
            foreach (var write in installed.AsEnumerable().Reverse())
                try
                {
                    if (write.Old == null) File.Delete(write.Path);
                    else Put(write.Path, write.Old, moveFile);
                }
                catch (Exception rollbackError)
                { errors.Add(new IOException($"Could not restore '{write.Path}' after the update failed.", rollbackError)); }
            if (errors.Count > 1) throw new AggregateException("The update failed, and one or more files could not be restored.", errors);
            throw new IOException("The update failed; files written earlier in the update were restored.", installError);
        }
    }

    internal static void Put(string path, byte[] bytes, Action<string, string, bool>? moveFile = null)
    {
        moveFile ??= static (source, destination, overwrite) => File.Move(source, destination, overwrite);
        string temp = path + ".new";
        try
        {
            File.WriteAllBytes(temp, bytes);
            try { moveFile(temp, path, true); }
            catch (Exception replaceError) when (File.Exists(path))
            {
                string aside = Aside(path);
                moveFile(path, aside, false); // a loaded DLL: renamed out of the way, deleted at the next start
                try { moveFile(temp, path, false); }
                catch (Exception installError)
                {
                    if (File.Exists(path))
                        throw new AggregateException($"Could not install '{path}'; the previous file remains at '{aside}'.", replaceError, installError);
                    try { moveFile(aside, path, false); }
                    catch (Exception restoreError)
                    { throw new AggregateException($"Could not install '{path}' or restore it; the previous file remains at '{aside}'.", replaceError, installError, restoreError); }
                    throw new IOException($"Could not install '{path}'; the previous file was restored.", installError);
                }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    static string Aside(string path)
    {
        for (int i = 0; ; i++)
        {
            string old = path + ".qol-update-old" + (i == 0 ? "" : i.ToString());
            if (!File.Exists(old)) return old;
        }
    }

    /// Only the updater's explicitly marked aside files for its own known assets; never delete a user's generic .old backup.
    internal static IEnumerable<string> OldUpdateFiles(string pluginDir)
    {
        if (!Directory.Exists(pluginDir)) yield break;
        foreach (var name in Files)
            foreach (var path in Directory.EnumerateFiles(pluginDir, name + ".qol-update-old*"))
                // A backup may be the only surviving copy if a previous replacement could not
                // restore its old file. Keep it unless the destination now exists.
                if (Regex.IsMatch(Path.GetFileName(path), "^" + Regex.Escape(name) + @"\.qol-update-old\d*$")
                    && File.Exists(Path.Combine(pluginDir, name)))
                    yield return path;
    }
}
