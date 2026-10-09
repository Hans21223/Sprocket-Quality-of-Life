using System.Globalization;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using SprocketModAPI;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace SprocketQoL;

/// Sprocket Mod API (github.com/furryaxw/SprocketModAPI), when it's installed: this mod's settings on its page in the
/// API's Mod menu, kept in step with the BepInEx config file both ways, and its keys in the API's keybinding window.
/// Optional: without the API none of this runs. No API type is named outside Link, and Link's code only runs once the
/// API is loaded, so the mod still loads without the API's DLL. The same file is in each of these mods, by namespace.
internal static class ModApi
{
    internal const string Guid = "furryaxw.sprocket-mod-api";
    // The API's input contexts (InputContextMask), for RegisterKey.
    internal const int Gameplay = 1, Designer = 2, MainMenu = 4, OtherMenu = 64;

    static ManualLogSource? log;
    static readonly HashSet<string> failed = new();

    /// The API is loaded (it loads first when it's there: each mod declares it a soft dependency).
    internal static bool Available => IL2CPPChainloader.Instance?.Plugins?.ContainsKey(Guid) == true;

    /// This mod's config entries on its page in the API's Mod menu (`include`: which ones; all by default). A number
    /// is a slider with a range (`range`, or the entry's own AcceptableValueRange), else typed text; a string can be
    /// offered as a list of (value, label).
    internal static void ShareConfig(ManualLogSource source, ConfigFile config, string title,
        Func<ConfigDefinition, bool>? include = null,
        Func<ConfigDefinition, (double Min, double Max, double Step)?>? range = null,
        Func<ConfigDefinition, IReadOnlyList<(string Value, string Label)>?>? choices = null)
    {
        log ??= source;
        if (Available) Run("settings", () => Link.ShareConfig(config, title, include, range, choices));
    }

    /// A key the player can rebind in the API's keybinding window, or null without the API. `defaultText`: "F9",
    /// "Ctrl+J", "Numpad1"; "" for none. `contexts`: where the API lists it as active (Gameplay, Designer, ...).
    internal static object? RegisterKey(ManualLogSource source, string id, string name, string group, string defaultText, int contexts)
    {
        log ??= source;
        return Available ? Get("keys", () => Link.RegisterKey(id, name, group, defaultText, contexts)) : null;
    }

    /// The key's first or second binding went down this frame, with that binding's modifiers held. Other modifiers may
    /// be held too (tools read Ctrl as "the other way"), so this is checked here rather than by the API's exact match.
    internal static bool KeyPressed(object handle) => Get("keys", () => Link.Pressed(handle));

    /// The first binding as text ("Ctrl+J"), "" when unbound, null when it can't be written so (a mouse button).
    internal static string? KeyText(object handle) => Get("keys", () => Link.Text(handle));

    internal static void SetKeyText(object handle, string text) => Run("keys", () => Link.SetText(handle, text));

    /// The bindings as the API shows them ("Left Ctrl + J", "Unbound").
    internal static string KeyShown(object handle) => Get("keys", () => Link.Shown(handle)) ?? "?";

    /// "Ctrl+Shift+J" -> J with Ctrl and Shift; an unknown key name -> Key.None.
    internal static (Key Key, bool Ctrl, bool Shift, bool Alt) Parse(string? text)
    {
        bool ctrl = false, shift = false, alt = false;
        var key = Key.None;
        foreach (var part in (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (Enum.TryParse<Key>(part, true, out var k)) key = k;
        }
        return (key, ctrl, shift, alt);
    }

    internal static string Format(Key key, bool ctrl, bool shift, bool alt) =>
        key == Key.None ? "" : $"{(ctrl ? "Ctrl+" : "")}{(shift ? "Shift+" : "")}{(alt ? "Alt+" : "")}{key}";

    static void Run(string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { if (failed.Add(what)) log?.LogWarning($"Sprocket Mod API {what}: {ex.Message}"); }
    }

    static T? Get<T>(string what, Func<T> f)
    {
        try { return f(); }
        catch (Exception ex) { if (failed.Add(what)) log?.LogWarning($"Sprocket Mod API {what}: {ex.Message}"); return default; }
    }

    /// Everything that names an API type: only reached when the API is loaded.
    static class Link
    {
        sealed record Shared(ConfigEntryBase Entry, ModConfigEntryDefinition Definition,
            Action<IModConfigRegistration> Push, Action<IModConfigRegistration> Pull);

        internal static void ShareConfig(ConfigFile config, string title, Func<ConfigDefinition, bool>? include,
            Func<ConfigDefinition, (double Min, double Max, double Step)?>? range,
            Func<ConfigDefinition, IReadOnlyList<(string Value, string Label)>?>? choices)
        {
            if (!SprocketApi.TryGetService<IModConfigService>(out var service) || service == null) return;
            var shared = new Dictionary<string, Shared>(StringComparer.Ordinal);
            var sections = new List<ModConfigSectionDefinition>();
            var sectionIds = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in config)
            {
                var def = pair.Key;
                if (include != null && !include(def)) continue;
                if (!sectionIds.TryGetValue(def.Section, out var sectionId))
                {
                    sectionId = Id(def.Section, sectionIds.Values);
                    sectionIds[def.Section] = sectionId;
                    sections.Add(new ModConfigSectionDefinition { Id = sectionId, Title = def.Section });
                }
                var key = Id(def.Section + "." + def.Key, shared.Keys);
                if (Describe(key, sectionId, def, pair.Value, range?.Invoke(def), choices?.Invoke(def)) is { } s) shared[key] = s;
            }
            if (shared.Count == 0) return;
            var registration = service.Register(new ModConfigDefinition
            {
                DisplayName = title,
                Sections = sections.Where(x => shared.Values.Any(s => s.Definition.SectionId == x.Id)).ToList(),
                Entries = shared.Values.Select(s => s.Definition).ToList(),
            });
            string modId = registration.Snapshot.ModId;
            bool syncing = false;
            void Sync(Shared s, bool push)
            {
                if (syncing) return;
                syncing = true;
                try { if (push) s.Push(registration); else s.Pull(registration); }
                catch (Exception) { if (!push) try { s.Push(registration); } catch (Exception) { } } // turned down: the file's value back
                finally { syncing = false; }
            }
            // The config file is the truth at the start; then each side follows the other.
            foreach (var s in shared.Values) Sync(s, push: true);
            config.SettingChanged += (_, e) =>
            {
                foreach (var s in shared.Values) if (ReferenceEquals(s.Entry, e.ChangedSetting)) Sync(s, push: true);
            };
            service.Changed += e =>
            {
                if (e.ModId == modId && shared.TryGetValue(e.Key, out var s)) Sync(s, push: false);
            };
        }

        /// An id the API takes (letters, digits, - _ . and at most 64), unique among `taken`.
        static string Id(string text, IEnumerable<string> taken)
        {
            var id = new string(text.Select(c => (c < 128 && char.IsLetterOrDigit(c)) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
            if (id.Length == 0) id = "entry";
            if (id.Length > 60) id = id[..60];
            var all = taken.ToHashSet(StringComparer.Ordinal);
            var unique = id;
            for (int n = 2; all.Contains(unique); n++) unique = $"{id}-{n}";
            return unique;
        }

        static Shared? Describe(string key, string section, ConfigDefinition def, ConfigEntryBase entry,
            (double Min, double Max, double Step)? range, IReadOnlyList<(string Value, string Label)>? labels)
        {
            string name = def.Key, about = entry.Description?.Description ?? "";
            var type = entry.SettingType;
            if (type == typeof(bool))
                return new(entry, ModConfigEntryDefinition.Toggle(key, name, (bool)entry.DefaultValue, about, section),
                    r => r.SetBool(key, (bool)entry.BoxedValue), r => entry.BoxedValue = r.GetBool(key));
            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                return new(entry, ModConfigEntryDefinition.Choice(key, name, entry.DefaultValue.ToString()!, names, about, section),
                    r => r.SetText(key, entry.BoxedValue.ToString()!), r => entry.BoxedValue = Enum.Parse(type, r.GetText(key)));
            }
            if (type == typeof(string) && labels is { Count: > 0 })
            {
                string Label(object value) => labels.FirstOrDefault(l => l.Value == (string)value).Label ?? (string)value;
                var options = labels.Select(l => l.Label).Distinct().ToList();
                if (!options.Contains(Label(entry.DefaultValue))) options.Insert(0, Label(entry.DefaultValue));
                return new(entry, ModConfigEntryDefinition.Choice(key, name, Label(entry.DefaultValue), options, about, section),
                    r => { var label = Label(entry.BoxedValue); if (options.Contains(label)) r.SetText(key, label); },
                    r => { var label = r.GetText(key); entry.BoxedValue = labels.FirstOrDefault(l => l.Label == label).Value ?? label; });
            }
            if (entry.Description?.AcceptableValues is { } acceptable && acceptable.GetType().GetProperty("AcceptableValues")?.GetValue(acceptable) is Array listed)
            {
                var values = listed.Cast<object>().ToList();
                var options = values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)!).Distinct().ToList();
                string Text(object v) => Convert.ToString(v, CultureInfo.InvariantCulture)!;
                return new(entry, ModConfigEntryDefinition.Choice(key, name, Text(entry.DefaultValue), options, about, section),
                    r => r.SetText(key, Text(entry.BoxedValue)),
                    r => { var text = r.GetText(key); if (values.FirstOrDefault(v => Text(v) == text) is { } v) entry.BoxedValue = v; });
            }
            if (IsNumber(type))
            {
                if (range == null && entry.Description?.AcceptableValues is { } bounds && bounds.GetType().GetProperty("MinValue")?.GetValue(bounds) is { } lo
                    && bounds.GetType().GetProperty("MaxValue")?.GetValue(bounds) is { } hi)
                {
                    double min = Convert.ToDouble(lo, CultureInfo.InvariantCulture), max = Convert.ToDouble(hi, CultureInfo.InvariantCulture);
                    range = (min, max, IsWhole(type) ? 1 : Math.Max((max - min) / 100, 0.001));
                }
                if (range is { } r0 && r0.Max > r0.Min && r0.Step > 0)
                {
                    double Clamp(object v) => Math.Clamp(Convert.ToDouble(v, CultureInfo.InvariantCulture), r0.Min, r0.Max);
                    return new(entry, ModConfigEntryDefinition.Slider(key, name, Clamp(entry.DefaultValue), r0.Min, r0.Max, r0.Step, about, section),
                        r => r.SetNumber(key, Clamp(entry.BoxedValue)), r => entry.BoxedValue = Number(r.GetNumber(key), type));
                }
                string Text(object v) => Convert.ToString(v, CultureInfo.InvariantCulture)!;
                return new(entry, ModConfigEntryDefinition.Text(key, name, Text(entry.DefaultValue), 32, about, section),
                    r => r.SetText(key, Text(entry.BoxedValue)),
                    r => entry.BoxedValue = Number(double.Parse(r.GetText(key).Replace(',', '.'), CultureInfo.InvariantCulture), type));
            }
            if (type == typeof(string))
            {
                int longest = Math.Max(512, Math.Max(((string)entry.DefaultValue).Length, ((string)entry.BoxedValue).Length) * 2);
                return new(entry, ModConfigEntryDefinition.Text(key, name, (string)entry.DefaultValue, longest, about, section),
                    r => r.SetText(key, (string)entry.BoxedValue), r => entry.BoxedValue = r.GetText(key));
            }
            return null; // colours, vectors, key shortcuts: not on the page
        }

        static bool IsWhole(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);
        static bool IsNumber(Type t) => IsWhole(t) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);
        static object Number(double value, Type type) => Convert.ChangeType(IsWhole(type) ? Math.Round(value) : value, type, CultureInfo.InvariantCulture);

        // ---------- keys ----------

        internal static object? RegisterKey(string id, string name, string group, string defaultText, int contexts)
        {
            if (!SprocketApi.TryGetService<IInputService>(out var input) || input == null) return null;
            return input.RegisterAction(new ModActionDefinition
            {
                ActionId = id, DisplayName = name, Category = group,
                DefaultPrimary = Chord(defaultText) ?? default,
                Contexts = (InputContextMask)contexts,
            });
        }

        static KeyChord? Chord(string? text)
        {
            var (key, ctrl, shift, alt) = Parse(text);
            if (key == Key.None) return null;
            var name = key is >= Key.Digit1 and <= Key.Digit0 ? ((int)(key - Key.Digit1 + 1) % 10).ToString() : char.ToLowerInvariant(key.ToString()[0]) + key.ToString()[1..];
            var mods = (ctrl ? ModifierKeys.AnyCtrl : 0) | (shift ? ModifierKeys.AnyShift : 0) | (alt ? ModifierKeys.AnyAlt : 0);
            return new KeyChord("<Keyboard>/" + name, mods);
        }

        static Key KeyOf(string control)
        {
            if (control.Length == 1 && char.IsDigit(control[0])) return control[0] == '0' ? Key.Digit0 : Key.Digit1 + (control[0] - '1');
            return Enum.TryParse<Key>(control, true, out var key) ? key : Key.None;
        }

        static string Control(KeyChord chord, string device) =>
            chord.ControlPath.StartsWith(device, StringComparison.OrdinalIgnoreCase) ? chord.ControlPath[device.Length..] : "";

        static bool Held(ModifierKeys m, Keyboard keys)
        {
            bool Side(ModifierKeys left, ModifierKeys right, KeyControl l, KeyControl r) =>
                (m & (left | right)) == 0 || ((m & left) != 0 && l.isPressed) || ((m & right) != 0 && r.isPressed);
            return Side(ModifierKeys.LeftCtrl, ModifierKeys.RightCtrl, keys.leftCtrlKey, keys.rightCtrlKey)
                && Side(ModifierKeys.LeftShift, ModifierKeys.RightShift, keys.leftShiftKey, keys.rightShiftKey)
                && Side(ModifierKeys.LeftAlt, ModifierKeys.RightAlt, keys.leftAltKey, keys.rightAltKey);
        }

        static bool Down(KeyChord chord)
        {
            if (chord.IsEmpty || Keyboard.current is not { } keys) return false;
            ButtonControl? button = null;
            if (KeyOf(Control(chord, "<keyboard>/")) is var key && key != Key.None) button = keys[key];
            else if (Mouse.current is { } mouse)
                button = Control(chord, "<mouse>/") switch
                {
                    "leftbutton" => mouse.leftButton, "rightbutton" => mouse.rightButton, "middlebutton" => mouse.middleButton,
                    "forwardbutton" => mouse.forwardButton, "backbutton" => mouse.backButton, _ => null,
                };
            return button != null && button.wasPressedThisFrame && Held(chord.Modifiers, keys);
        }

        internal static bool Pressed(object handle) =>
            handle is IInputActionHandle h && (Down(h.Primary) || Down(h.Secondary));

        internal static string? Text(object handle)
        {
            if (handle is not IInputActionHandle h || h.Primary.IsEmpty) return "";
            var key = KeyOf(Control(h.Primary, "<keyboard>/"));
            if (key == Key.None) return null;
            var m = h.Primary.Modifiers;
            return Format(key, (m & ModifierKeys.AnyCtrl) != 0, (m & ModifierKeys.AnyShift) != 0, (m & ModifierKeys.AnyAlt) != 0);
        }

        internal static void SetText(object handle, string text)
        {
            if (handle is IInputActionHandle h) h.SetBinding(0, Chord(text));
        }

        internal static string Shown(object handle) =>
            handle is not IInputActionHandle h ? "?" : !h.Primary.IsEmpty ? h.Primary.DisplayName : h.Secondary.DisplayName;
    }
}
