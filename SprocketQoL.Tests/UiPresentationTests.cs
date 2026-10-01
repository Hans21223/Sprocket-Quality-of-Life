using SprocketQoL;

static class UiPresentationTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool ok, string reason) { checks++; if (!ok) throw new Exception(reason); }
        var state = new UiPresentation.FoldState("Mesh tools|Merge add-ons into this|Drawing sheet (F9)|future section");
        Check(!state.IsOpen("shape.edit"), "old Mesh tools fold migrates to stable identity");
        Check(!state.IsOpen("Shape editing"), "renamed section keeps fold");
        Check(!state.IsOpen("Merge add-ons"), "merge panel variants share one preference");
        Check(!state.IsOpen("drawing.export"), "drawing fold survives renamed title");
        Check(!state.IsOpen("future section"), "unknown future section is preserved");
        Check(state.SetOpen("Mesh tools", true), "open migrated section");
        Check(!state.SetOpen("shape.edit", true), "duplicate open is a no-op");
        Check(state.IsOpen("Shape editing"), "same state under friendly title");
        Check(state.SetOpen("Shape editing", false), "close via friendly title");
        var restored = new UiPresentation.FoldState(state.Serialize());
        Check(!restored.IsOpen("Mesh tools") && !restored.IsOpen("future section"), "serialize roundtrip preserves folds");
        Check(state.Serialize() == restored.Serialize(), "serialization is deterministic");
        Check(UiPresentation.Sections.Select(s => s.Id).Distinct().Count() == UiPresentation.Sections.Length, "unique stable section IDs");
        foreach (var section in UiPresentation.Sections)
        {
            Check(UiPresentation.Resolve(section.Id).Title == section.Title, "identity resolves current heading");
            foreach (string old in section.LegacyNames) Check(UiPresentation.Resolve(old).Id == section.Id, "legacy alias resolves");
        }
        Check(UiPresentation.InfoLines("Short help", 320) == 1, "short help stays compact");
        Check(UiPresentation.InfoLines("A\nB\nC", 320) == 3, "explicit newlines reserve three rows");
        Check(UiPresentation.InfoLines("hello world", 80) == 2, "word wrap reserves a second row");
        Check(UiPresentation.InfoLines(new string('W', 45), 160) == 3, "long unbroken word does not clip");
        Check(UiPresentation.InfoLines("short", 320, 4) == 4, "caller's minimum is respected");
        Check(UiPresentation.InfoLines("e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301e\u0301", 80) == 1, "combining characters stay with their base");
        Check(UiPresentation.InfoLines("坦克坦克坦克", 80) == 2, "wide Unicode text has space");
        const string help = "Select faces, then separate them into a new add-on. Attached parts stay with their parent.";
        int previous = int.MaxValue;
        foreach (int width in new[] { 80, 120, 160, 240, 320, 480, 800 })
        {
            int lines = UiPresentation.InfoLines(help, width);
            Check(lines <= previous, "wider panels do not reserve more rows");
            previous = lines;
        }
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, -100, 0, 1 })
            Check(UiPresentation.InfoLines(help, bad) == UiPresentation.InfoLines(help, 280), "unknown native width uses fallback");
        foreach (var (width, height) in new[] { (1920, 1080), (1280, 720), (800, 450), (320, 240) })
            foreach (var inspector in new UiPresentation.ScreenBox?[]
                { null, new(width - 300, 50, 300, height - 100), new(0, 50, 260, height - 100), new(width / 2 - 120, 50, 240, height - 100) })
            {
                var placed = UiPresentation.PlaceHelp(width, height, inspector);
                Check(placed.X >= 0 && placed.Y >= 0 && placed.Right <= width && placed.Bottom <= height, "help remains on screen after resize/reposition");
                Check(placed.Width > 0 && placed.Height > 0, "help retains positive dimensions");
            }
        var rightPanel = new UiPresentation.ScreenBox(1450, 60, 470, 930);
        var leftHelp = UiPresentation.PlaceHelp(1920, 1080, rightPanel);
        Check(leftHelp.Right <= rightPanel.X, "right inspector leaves help on its left");
        var leftPanel = new UiPresentation.ScreenBox(0, 60, 470, 930);
        var rightHelp = UiPresentation.PlaceHelp(1920, 1080, leftPanel);
        Check(rightHelp.X >= leftPanel.Right, "left inspector leaves help on its right");
        float[] rowHeights = { 18, 40, 18, 60, 18, 18, 18 };
        var pages = UiPresentation.HelpPages(rowHeights, 80);
        Check(pages.Length > 1, "short window creates more than one help page");
        var covered = pages.SelectMany(p => Enumerable.Range(p.Start, p.Count)).ToArray();
        Check(covered.SequenceEqual(Enumerable.Range(0, rowHeights.Length)), "pages preserve every group once and in order");
        foreach (var page in pages) Check(rowHeights.Skip(page.Start).Take(page.Count).Sum() <= 80, "normal groups fit their page");
        Check(UiPresentation.HelpPages(Array.Empty<float>(), 80).Length == 0, "no empty help page");
        Check(UiPresentation.HelpPages(new[] { 200f, 18f }, 80).Length == 2, "oversized group does not swallow the following group");
        string longGroup = "Camera: front side top opposite orthographic zoom";
        var blocks = UiPresentation.FitHelp(longGroup, s => s.Length <= 12);
        Check(string.Join(" ", blocks) == longGroup, "splitting an oversized shortcut group preserves every word");
        Check(blocks.All(s => s.Length <= 12), "oversized group fragments fit the measured limit");
        string combined = string.Concat(Enumerable.Repeat("e\u0301", 12));
        var unicodeBlocks = UiPresentation.FitHelp(combined, s => System.Globalization.StringInfo.ParseCombiningCharacters(s).Length <= 3);
        Check(string.Concat(unicodeBlocks) == combined, "oversized unbroken Unicode string is preserved");
        Check(unicodeBlocks.All(s => s.StartsWith("e", StringComparison.Ordinal) && s.EndsWith("\u0301", StringComparison.Ordinal)), "grapheme boundaries survive splitting");
        Console.WriteLine($"UI_PRESENTATION_TESTS_OK: {checks} checks (fold migration, width changes, Unicode)");
    }
}
