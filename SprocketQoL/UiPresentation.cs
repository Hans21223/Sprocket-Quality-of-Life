using System.Globalization;

namespace SprocketQoL;

/// Presentation rules independent of the game's inspector implementation.
internal static class UiPresentation
{
    internal sealed record Section(string Id, string Title, params string[] LegacyNames);
    internal static readonly Section[] Sections =
    {
        new("shape.edit", "Shape editing", "Mesh tools"),
        new("shape.round", "Edge rounding", "Edge rounding"),
        new("shape.split", "Subdivision", "Subdivision"),
        new("shape.select", "Selection and movement", "Selection and movement"),
        new("shape.bridge", "Bridge and circle", "Bridge and circle"),
        new("shape.mirror", "Mirror and symmetry", "Mirror fixes"),
        new("view.lighting", "View and lighting", "View and lighting"),
        new("parts.merge", "Join add-ons", "Merge add-ons", "Merge add-ons into this"),
        new("parts.cut", "Cut with an add-on", "Cut with this add-on"),
        new("faces.merge", "Merge faces", "Merge faces"),
        new("faces.separate", "Separate faces", "Separate"),
        new("holes.settings", "Hole settings", "Hole quality"),
        new("turret.convert", "Convert turret to add-on", "Turret to Add-on"),
        new("turret.drives", "Repair turret drives", "Mirrored turret drives"),
        new("gun.measure", "Gun dimensions", "Gun length", "Barrel measurements"),
        new("vehicle.speed", "Speed estimates", "Speed & acceleration"),
        new("parts.paint", "Part paint", "Own paint"),
        new("edit.restore", "Restore previous design", "Undo last Quality of Life edit"),
        new("drawing.export", "Drawing sheet", "Drawing sheet (F9)", "Drawing export (F9)"),
        new("model.obj", "OBJ export / import", "OBJ export / import (F10)"),
    };

    static readonly Dictionary<string, Section> byName = BuildNames();
    static Dictionary<string, Section> BuildNames()
    {
        var names = new Dictionary<string, Section>(StringComparer.Ordinal);
        foreach (var section in Sections)
        {
            names.Add(section.Id, section);
            names[section.Title] = section;
            foreach (string legacy in section.LegacyNames) names[legacy] = section;
        }
        return names;
    }
    internal static Section Resolve(string name) => byName.TryGetValue(name, out var section) ? section : new(name, name);

    internal readonly record struct ScreenBox(float X, float Y, float Width, float Height)
    {
        internal float Right => X + Width;
        internal float Bottom => Y + Height;
    }

    /// Place help beside the measured inspector, with a bounded fallback when the native layout is unavailable.
    internal static ScreenBox PlaceHelp(float screenWidth, float screenHeight, ScreenBox? inspector)
    {
        float sw = float.IsFinite(screenWidth) ? Math.Max(1, screenWidth) : 1280;
        float sh = float.IsFinite(screenHeight) ? Math.Max(1, screenHeight) : 720;
        float margin = Math.Min(12, Math.Min(sw, sh) / 10);
        float available = Math.Max(1, sw - 2 * margin), width = Math.Min(600, available);
        float x = margin, y = Math.Min(90, sh / 4), bottom = sh - margin;
        if (inspector is { } panel && float.IsFinite(panel.X) && float.IsFinite(panel.Right) && panel.Width > 0)
        {
            float leftSpace = panel.X - 2 * margin, rightSpace = sw - panel.Right - 2 * margin;
            float room = Math.Max(leftSpace, rightSpace);
            if (room >= Math.Min(200, available))
            {
                width = Math.Min(width, room);
                x = leftSpace >= rightSpace ? panel.X - margin - width : panel.Right + margin;
            }
            if (float.IsFinite(panel.Bottom) && panel.Bottom > y + 100) bottom = Math.Min(bottom, panel.Bottom);
        }
        x = Math.Clamp(x, margin, Math.Max(margin, sw - margin - width));
        return new(x, y, width, Math.Max(1, bottom - y));
    }

    /// Pages retain whole shortcut groups instead of clipping the tail on short windows.
    internal static (int Start, int Count)[] HelpPages(IReadOnlyList<float> heights, float availableHeight)
    {
        var pages = new List<(int, int)>();
        float limit = float.IsFinite(availableHeight) ? Math.Max(1, availableHeight) : 400;
        int first = 0; float used = 0;
        for (int i = 0; i < heights.Count; i++)
        {
            float height = float.IsFinite(heights[i]) ? Math.Max(1, heights[i]) : 18;
            if (i > first && used + height > limit) { pages.Add((first, i - first)); first = i; used = 0; }
            used += height;
        }
        if (first < heights.Count) pages.Add((first, heights.Count - first));
        return pages.ToArray();
    }

    /// Split an oversized group using the backend's actual font measurement, preserving all words/graphemes.
    internal static string[] FitHelp(string text, Func<string, bool> fits)
    {
        if (fits(text)) return new[] { text };
        var blocks = new List<string>();
        string current = "";
        foreach (string word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (fits(candidate)) { current = candidate; continue; }
            if (current.Length > 0) { blocks.Add(current); current = ""; }
            var elements = StringInfo.GetTextElementEnumerator(word);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (current.Length > 0 && !fits(current + element)) { blocks.Add(current); current = ""; }
                current += element;
            }
        }
        if (current.Length > 0) blocks.Add(current);
        return blocks.ToArray();
    }

    /// Persist identities, not visible titles, so wording changes do not reset a player's folded panels.
    internal sealed class FoldState
    {
        readonly HashSet<string> closed;
        internal FoldState(string? saved) => closed = (saved ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => Resolve(name).Id).ToHashSet(StringComparer.Ordinal);
        internal bool IsOpen(string name) => !closed.Contains(Resolve(name).Id);
        internal bool SetOpen(string name, bool open) => open ? closed.Remove(Resolve(name).Id) : closed.Add(Resolve(name).Id);
        internal string Serialize() => string.Join("|", closed.OrderBy(id => id, StringComparer.Ordinal));
    }

    /// Native information fields require a line count. Reserve space from the current field width,
    /// including explicit newlines and unbroken words, rather than a fixed inspector-width assumption.
    internal static int InfoLines(string text, float fieldWidth, int minimum = 1)
    {
        float width = float.IsFinite(fieldWidth) && fieldWidth >= 80 ? Math.Min(fieldWidth, 16384) : 280;
        int budget = Math.Max(8, (int)(width / 8));
        int rows = 0;
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            int used = 0;
            rows++;
            foreach (string word in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                int length = 0;
                foreach (int start in StringInfo.ParseCombiningCharacters(word)) length += word[start] > 255 ? 2 : 1;
                if (used > 0 && used + 1 + length > budget) { rows++; used = 0; }
                if (used > 0) used++;
                while (length > budget) { rows++; length -= budget; }
                used += length;
            }
        }
        return Math.Max(Math.Max(1, minimum), rows);
    }
}
