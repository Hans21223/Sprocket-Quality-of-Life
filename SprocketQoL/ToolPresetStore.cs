using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SprocketQoL;

/// Named, versioned snapshots of explicitly registered tool settings. Does not depend on Unity.
public static class ToolPresetStore
{
    public sealed record Preset(string Id, string Name, string Group, DateTime CreatedUtc, Dictionary<string, JsonElement> Values);
    public static string DirectoryPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SprocketQoL", "ToolPresets");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static IReadOnlyList<Preset> List(string? group = null)
    {
        if (!Directory.Exists(DirectoryPath)) return Array.Empty<Preset>();
        var found = new List<Preset>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            try
            {
                if (new FileInfo(file).Length > 1024 * 1024) continue;
                var p = JsonSerializer.Deserialize<Preset>(File.ReadAllText(file));
                if (p != null && p.Id == Path.GetFileNameWithoutExtension(file) && p.Values != null &&
                    (group == null || p.Group == group)) found.Add(p);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return found.OrderBy(p => p.Group, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static Preset Save(string group, string name, IReadOnlyDictionary<string, JsonElement> values)
    {
        group = Name(group); name = Name(name);
        if (values.Count == 0) throw new InvalidOperationException("This tool group has no settings to save.");
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group + "\n" + name))).ToLowerInvariant();
        var p = new Preset(id, name, group, DateTime.UtcNow, values.ToDictionary(v => v.Key, v => v.Value.Clone(), StringComparer.Ordinal));
        WorkshopStorage.AtomicWrite(Path.Combine(DirectoryPath, id + ".json"), JsonSerializer.Serialize(p, Json));
        return p;
    }

    public static bool Delete(string id)
    {
        if (id.Length != 64 || !id.All(Uri.IsHexDigit)) return false;
        string file = Path.Combine(DirectoryPath, id + ".json");
        if (!File.Exists(file)) return false;
        File.Delete(file); return true;
    }

    static string Name(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Length > 100 || value.Any(char.IsControl))
            throw new ArgumentException("Use a name between 1 and 100 characters without control characters.");
        return value;
    }
}
