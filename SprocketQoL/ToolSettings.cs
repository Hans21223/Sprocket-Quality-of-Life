using System.Reflection;
using System.Text.Json;
using BepInEx.Configuration;

namespace SprocketQoL;

/// Only registered editor preferences are persisted; transient state never becomes a setting.
internal static class ToolSettings
{
    sealed record Setting(string Group, string Key, Func<JsonElement> Read, Action<JsonElement> Write, Action Bind, Func<bool> Flush, Func<object?> Value);
    static readonly Dictionary<string, Setting> settings = new(StringComparer.Ordinal);
    static ConfigFile? config;
    static float nextSave;
    static (float Until, int Hash) fingerprint;
    internal static IReadOnlyList<string> Groups => settings.Values.Select(s => s.Group).Distinct().OrderBy(s => s).ToArray();

    internal static void Load(ConfigFile file) { config = file; foreach (var setting in settings.Values) setting.Bind(); }

    internal static void Register<T>(string group, string key, Func<T> get, Action<T> set, Func<T, bool>? valid = null)
    {
        string full = group + "/" + key;
        if (settings.ContainsKey(full)) return;
        ConfigEntry<T>? entry = null;
        T initial = get();
        bool Valid(T value) => !(value is float f && !float.IsFinite(f)) && !(value is double d && !double.IsFinite(d)) && valid?.Invoke(value) != false;
        void Bind()
        {
            if (config == null || entry != null) return;
            entry = config.Bind("Tool preferences: " + group, key, initial, "Last used value for " + group + ". Included in named workshop presets.");
            if (Valid(entry.Value)) set(entry.Value); else entry.Value = initial;
        }
        var s = new Setting(group, key, () => JsonSerializer.SerializeToElement(get()), element =>
        {
            var value = element.Deserialize<T>();
            if (value == null || !Valid(value)) throw new FormatException("Invalid preset value for " + full + ".");
            set(value);
        }, Bind, () =>
        {
            if (entry == null || !Valid(get()) || EqualityComparer<T>.Default.Equals(entry.Value, get())) return false;
            entry.Value = get(); return true;
        }, () => get());
        settings.Add(full, s); Bind();
    }

    internal static void RegisterFields(Type type, string section, params string[] fields)
    {
        foreach (string name in fields)
        {
            var field = type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new ArgumentException($"Unknown tool setting {type.Name}.{name}.");
            if (field.IsInitOnly || !(field.FieldType.IsEnum || field.FieldType == typeof(bool) || field.FieldType == typeof(int) || field.FieldType == typeof(float)))
                throw new ArgumentException($"Tool setting {type.Name}.{name} must be a mutable static bool, int, float or enum.");
            typeof(ToolSettings).GetMethod(nameof(RegisterField), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(field.FieldType).Invoke(null, new object[] { section, field });
        }
    }

    static void RegisterField<T>(string group, FieldInfo field) => Register(group, field.Name,
        () => (T)field.GetValue(null)!, value => field.SetValue(null, value), value =>
        {
            if (typeof(T).IsEnum) return Enum.IsDefined(typeof(T), value!);
            return value is not (int or float) || ToolSettingRanges.Valid(field.Name, Convert.ToDouble(value));
        });

    /// A number that changes when any tool preference does (worked out at most 10 times a second), so a preview keyed
    /// on it is made again after a setting changes.
    internal static int Fingerprint()
    {
        if (UnityEngine.Time.unscaledTime < fingerprint.Until) return fingerprint.Hash;
        var hash = new HashCode();
        foreach (var s in settings.Values) hash.Add(s.Value());
        fingerprint = (UnityEngine.Time.unscaledTime + 0.1f, hash.ToHashCode());
        return fingerprint.Hash;
    }

    internal static Dictionary<string, JsonElement> Snapshot(string? group = null) => settings
        .Where(p => group == null || p.Value.Group == group).ToDictionary(p => p.Key, p => p.Value.Read().Clone(), StringComparer.Ordinal);

    internal static int Apply(IReadOnlyDictionary<string, JsonElement> values)
    {
        // Apply as a transaction: reject an invalid value and put all already changed preferences back.
        var previous = Snapshot(); var changed = new List<Setting>();
        try
        {
            foreach (var p in values)
                if (settings.TryGetValue(p.Key, out var setting)) { changed.Add(setting); setting.Write(p.Value); }
        }
        catch { foreach (var setting in changed) setting.Write(previous[setting.Group + "/" + setting.Key]); throw; }
        Flush(); return changed.Count;
    }

    internal static void Tick()
    {
        if (UnityEngine.Time.unscaledTime < nextSave) return;
        nextSave = UnityEngine.Time.unscaledTime + 0.5f; Flush();
    }
    internal static void Flush()
    {
        if (config == null) return;
        bool automatic = config.SaveOnConfigSet, changed = false;
        config.SaveOnConfigSet = false;
        try { foreach (var s in settings.Values) changed |= s.Flush(); }
        finally { config.SaveOnConfigSet = automatic; }
        if (changed) config.Save();
    }
}
