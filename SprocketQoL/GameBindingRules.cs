namespace SprocketQoL;

/// Rules shared by the native-control settings UI and its offline checks. Pointer axes and composite containers
/// must keep their native value types; only the individual digital inputs can be assigned a key or mouse button.
internal static class GameBindingRules
{
    internal static bool Editable(string? defaultPath, bool isComposite) => !isComposite && ButtonPath(defaultPath);

    internal static bool ButtonPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string p = path.Trim();
        if (p.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase))
        {
            string key = p[11..];
            return key.Length > 0 && key.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')
                && !key.Equals("anyKey", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("imeSelected", StringComparison.OrdinalIgnoreCase);
        }
        return p.ToLowerInvariant() is "<mouse>/leftbutton" or "<mouse>/rightbutton" or "<mouse>/middlebutton"
            or "<mouse>/backbutton" or "<mouse>/forwardbutton";
    }

    internal static bool Overlaps(string? a, string? b)
    {
        if (!ButtonPath(a) || !ButtonPath(b)) return false;
        a = Normalize(a!); b = Normalize(b!);
        if (a == b) return true;
        foreach (string modifier in new[] { "ctrl", "shift", "alt", "meta" })
        {
            string prefix = "<keyboard>/";
            if (a == prefix + modifier && (b == prefix + "left" + modifier || b == prefix + "right" + modifier)
                || b == prefix + modifier && (a == prefix + "left" + modifier || a == prefix + "right" + modifier)) return true;
        }
        return false;
    }

    static string Normalize(string path) => path.Trim().ToLowerInvariant().Replace("control", "ctrl")
        .Replace("leftwindows", "leftmeta").Replace("rightwindows", "rightmeta")
        .Replace("leftcommand", "leftmeta").Replace("rightcommand", "rightmeta")
        .Replace("leftapple", "leftmeta").Replace("rightapple", "rightmeta");
}
