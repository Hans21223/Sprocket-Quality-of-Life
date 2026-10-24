using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SprocketQoL;

/// Disk-only workshop data. All user names are metadata; they are never used as paths.
public static class WorkshopStorage
{
    public sealed record Part(string Id, string Name, string Tags, DateTime CreatedUtc, int Count, string Json, bool History);
    public sealed record Backup(string Id, string Name, string Operation, DateTime CreatedUtc, bool Pinned, string Directory);
    sealed record BackupNote(string Name, string Operation, DateTime CreatedUtc, bool Pinned);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly Regex BackupId = new(@"^\d{8}-\d{6}-[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    public static string Root { get; set; } = AppDomain.CurrentDomain.BaseDirectory;
    public static string LibraryDirectory => Path.Combine(Root, "SprocketQoL", "Parts");
    public static string BackupDirectory => Path.Combine(Root, "SprocketQoLBackups");
    internal static long MaxPartBytes = 128L * 1024 * 1024; // the tests set it smaller

    /// A saved group as listed: all but the group itself (ReadPartJson reads that when it's needed). One too big to
    /// open is still listed, so it can be deleted.
    public sealed record PartInfo(string Id, string Name, string Tags, DateTime CreatedUtc, int Count, bool History, long Bytes)
    {
        public bool TooLarge => Bytes > MaxPartBytes;
    }

    public static IReadOnlyList<PartInfo> ListParts(bool history)
    {
        string dir = Path.Combine(LibraryDirectory, history ? "History" : "Saved");
        if (!Directory.Exists(dir)) return Array.Empty<PartInfo>();
        var result = new List<PartInfo>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
            try { if (ReadInfo(file, history) is { } p) result.Add(p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return result.OrderByDescending(p => p.CreatedUtc).ToArray();
    }

    /// The listing fields, from the start of the file only: they're written before the group, which can be 100 MB.
    static PartInfo? ReadInfo(string file, bool history)
    {
        long length = new FileInfo(file).Length;
        var head = new byte[(int)Math.Min(length, 64 * 1024)];
        using (var stream = File.OpenRead(file))
            for (int got = 0, n; got < head.Length && (n = stream.Read(head, got, head.Length - got)) > 0;) got += n;
        var reader = new Utf8JsonReader(head, isFinalBlock: head.Length == length, state: default);
        string? id = null, name = null, tags = null;
        DateTime created = default;
        int count = 0;
        bool? inHistory = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string property = reader.GetString()!;
            if (property == nameof(Part.Json)) break; // the group itself: left unread
            if (!reader.Read()) return null;
            switch (property)
            {
                case nameof(Part.Id): id = reader.GetString(); break;
                case nameof(Part.Name): name = reader.GetString(); break;
                case nameof(Part.Tags): tags = reader.GetString(); break;
                case nameof(Part.CreatedUtc): created = reader.GetDateTime(); break;
                case nameof(Part.Count): count = reader.GetInt32(); break;
                case nameof(Part.History): inHistory = reader.GetBoolean(); break;
                default: if (!reader.TrySkip()) return null; break;
            }
        }
        if (id != Path.GetFileNameWithoutExtension(file) || name == null || count <= 0 || inHistory is bool h && h != history) return null;
        return new(id, name, tags ?? "", created, count, history, length);
    }

    /// A listed group itself: the clipboard JSON it was saved from.
    public static string ReadPartJson(string id, bool history)
    {
        if (id.Length is not (32 or 64) || !id.All(Uri.IsHexDigit)) throw new ArgumentException("This part group isn't in the library.");
        string file = Path.Combine(LibraryDirectory, history ? "History" : "Saved", id + ".json");
        if (new FileInfo(file).Length > MaxPartBytes) throw new InvalidDataException("This part group is too large to open. Delete it here.");
        var part = JsonSerializer.Deserialize<Part>(File.ReadAllText(file));
        return part?.Id == id && !string.IsNullOrEmpty(part.Json) ? part.Json : throw new InvalidDataException("This part group can't be read. Delete it here.");
    }

    public static Part SavePart(string name, string json, string tags = "", bool history = false)
    {
        name = CleanName(name); tags = tags.Trim();
        if (tags.Length > 300) throw new ArgumentException("Keep tags under 300 characters.");
        if (Encoding.UTF8.GetByteCount(json) > MaxPartBytes) throw new ArgumentException("The part group is too large for the library.");
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("The clipboard is not an object.");
        int count = node["objects"]?.AsArray().Count ?? 0;
        if (count == 0) throw new FormatException("Copy some parts before saving a part group.");
        // Copy timestamps are metadata, not a different assembly. Re-copying a group updates one history slot.
        var fingerprint = JsonNode.Parse(node.ToJsonString())!.AsObject(); fingerprint.Remove("copiedAt");
        string id = history ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToJsonString()))).ToLowerInvariant()
            : Guid.NewGuid().ToString("N");
        var part = new Part(id, name, tags, DateTime.UtcNow, count, json, history);
        string dir = Path.Combine(LibraryDirectory, history ? "History" : "Saved");
        // The file holds the group as escaped text, which can be several times bigger: the list reads files up to the
        // same size, so what it would skip is refused here.
        string stored = JsonSerializer.Serialize(part, Json);
        if (Encoding.UTF8.GetByteCount(stored) > MaxPartBytes) throw new ArgumentException("The part group is too large for the library.");
        AtomicWrite(Path.Combine(dir, id + ".json"), stored);
        // Newest 30 by when each file was written (a copy saved again is rewritten), without reading every group back.
        if (history)
            foreach (string old in Directory.EnumerateFiles(dir, "*.json").OrderByDescending(File.GetLastWriteTimeUtc).Skip(30))
                DeletePart(Path.GetFileNameWithoutExtension(old), true);
        return part;
    }

    public static bool DeletePart(string id, bool history)
    {
        if (id.Length is not (32 or 64) || !id.All(Uri.IsHexDigit)) return false;
        string file = Path.Combine(LibraryDirectory, history ? "History" : "Saved", id + ".json");
        if (!File.Exists(file)) return false;
        File.Delete(file); return true;
    }

    public static IReadOnlyList<Backup> ListBackups()
    {
        if (!Directory.Exists(BackupDirectory)) return Array.Empty<Backup>();
        var result = new List<Backup>();
        foreach (string dir in Directory.EnumerateDirectories(BackupDirectory))
        {
            string id = Path.GetFileName(dir);
            if (!BackupId.IsMatch(id) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            string original = Path.Combine(dir, "original.blueprint");
            if (!File.Exists(original)) continue;
            try
            {
                BackupNote? note = null; bool unreadableNote = false;
                try { note = ReadNote(dir); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { unreadableNote = true; }
                string name = note?.Name ?? BlueprintName(original);
                string operation = note?.Operation ?? (File.Exists(Path.Combine(dir, "README.txt")) ? File.ReadAllText(Path.Combine(dir, "README.txt")).Split('.').FirstOrDefault() ?? "QoL edit" : "QoL edit");
                result.Add(new(id, name, operation, note?.CreatedUtc ?? Directory.GetCreationTimeUtc(dir), unreadableNote || note?.Pinned == true, dir));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return result.OrderByDescending(b => b.CreatedUtc).ToArray();
    }

    public static bool IsPinned(string directory)
    {
        string checkedPath = CheckedBackup(directory);
        try { return ReadNote(checkedPath)?.Pinned == true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return true; } // preserve an unreadable pin marker
    }

    public static void SetPinned(string id, bool pinned)
    {
        var b = ListBackups().FirstOrDefault(b => b.Id == id) ?? throw new FileNotFoundException("This backup is no longer available.");
        AtomicWrite(Path.Combine(b.Directory, "workshop.json"), JsonSerializer.Serialize(new BackupNote(b.Name, b.Operation, b.CreatedUtc, pinned), Json));
    }

    public static Backup SaveCheckpoint(string name, string blueprintJson)
    {
        name = CleanName(name); ValidateBlueprint(blueprintJson);
        string id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        string dir = Path.Combine(BackupDirectory, id);
        AtomicWrite(Path.Combine(dir, "original.blueprint"), blueprintJson);
        AtomicWrite(Path.Combine(dir, "edited.blueprint"), blueprintJson);
        AtomicWrite(Path.Combine(dir, "workshop.json"), JsonSerializer.Serialize(new BackupNote(name, "Named checkpoint", DateTime.UtcNow, true), Json));
        return ListBackups().First(b => b.Id == id);
    }

    /// Export a recovery copy into a faction, with a distinct filename and internal design name. Never overwrites.
    public static string RestoreCopy(string id, string factionDirectory, bool edited = false)
    {
        var b = ListBackups().FirstOrDefault(b => b.Id == id) ?? throw new FileNotFoundException("This backup is no longer available.");
        string source = Path.Combine(CheckedBackup(b.Directory), edited ? "edited.blueprint" : "original.blueprint");
        string json = File.ReadAllText(source); var node = ValidateBlueprint(json);
        string folder = Path.GetFullPath(Path.Combine(factionDirectory, "Blueprints", "Vehicles"));
        Directory.CreateDirectory(folder);
        string designName = node["header"]?["name"]?.GetValue<string>() ?? b.Name;
        string safe = string.Concat(designName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        if (safe.Length == 0) safe = "Recovered design";
        if (safe.Length > 80) safe = safe[..80];
        for (int n = 1; n <= 10000; n++)
        {
            string name = safe + " recovery" + (n == 1 ? "" : " " + n);
            string path = Path.Combine(folder, name + ".blueprint");
            if (File.Exists(path)) continue;
            node["header"]!["name"] = name;
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(node.ToJsonString(Json)); return path;
        }
        throw new IOException("There are too many recovery copies with this name.");
    }

    public static void Prune(int keep)
    {
        if (keep <= 0 || !Directory.Exists(BackupDirectory)) return;
        var unpinned = Directory.EnumerateDirectories(BackupDirectory).Where(d => BackupId.IsMatch(Path.GetFileName(d)))
            .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0 && !IsPinned(d))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(keep).ToArray();
        foreach (string dir in unpinned) Directory.Delete(CheckedBackup(dir), true);
    }

    public static string SaveSupportReport(string text)
    {
        string path = Path.Combine(Root, "SprocketQoL", "Reports", "support-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        AtomicWrite(path, text); return path;
    }

    public static void AtomicWrite(string path, string text)
    {
        string full = Path.GetFullPath(path), dir = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(dir);
        if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) throw new IOException("Workshop storage cannot write through a linked directory.");
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, text, new UTF8Encoding(false)); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    static string CheckedBackup(string directory)
    {
        string full = Path.GetFullPath(directory), root = Path.GetFullPath(BackupDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(full), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) || !BackupId.IsMatch(Path.GetFileName(full)))
            throw new IOException("The backup path is outside QoL's backup folder.");
        if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked backup folders are not supported.");
        return full;
    }
    static BackupNote? ReadNote(string directory)
    {
        string file = Path.Combine(directory, "workshop.json");
        if (!File.Exists(file)) return null;
        // A note that's there but not whole (`{}`, `null`, no pin written) counts as unreadable: its backup is kept.
        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var root = document.RootElement;
        var note = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(nameof(BackupNote.Pinned), out var pin)
                   && pin.ValueKind is JsonValueKind.True or JsonValueKind.False ? root.Deserialize<BackupNote>() : null;
        return note is { Name: not null, Operation: not null } && note.CreatedUtc != default ? note : throw new JsonException("workshop.json isn't a whole backup note");
    }
    static string BlueprintName(string path)
    {
        using var stream = File.OpenRead(path); using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty("header", out var header) && header.TryGetProperty("name", out var name) ? name.GetString() ?? "Untitled design" : "Untitled design";
    }
    static JsonObject ValidateBlueprint(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Invalid design snapshot.");
        if (node["header"] is not JsonObject || node["objects"] is not JsonArray) throw new FormatException("This is not a whole design snapshot.");
        return node;
    }
    static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 100 || name.Any(char.IsControl)) throw new ArgumentException("Use a name between 1 and 100 characters.");
        return name;
    }
}
