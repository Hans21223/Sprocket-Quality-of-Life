using SprocketQoL;

static class GameBindingTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool condition, string why) { checks++; if (!condition) throw new Exception("Game binding: " + why); }
        foreach (string path in new[] { "<Keyboard>/a", "<Keyboard>/leftCtrl", "<Keyboard>/ctrl", "<Keyboard>/shift", "<Keyboard>/alt",
            "<Keyboard>/escape", "<Keyboard>/backspace", "<Keyboard>/contextMenu", "<Keyboard>/numpadEnter", "<Mouse>/leftButton",
            "<Mouse>/rightButton", "<Mouse>/middleButton", "<Mouse>/backButton", "<Mouse>/forwardButton" })
        {
            Check(GameBindingRules.ButtonPath(path), "digital key or mouse control accepted: " + path);
            Check(GameBindingRules.Editable(path, false), "digital binding is editable: " + path);
            Check(!GameBindingRules.Editable(path, true), "composite parent cannot be replaced by a button: " + path);
            Check(GameBindingRules.Overlaps(path, path.ToUpperInvariant()), "path comparison is case insensitive: " + path);
        }
        foreach (string? path in new[] { null, "", " ", "<Mouse>/position", "<Mouse>/delta", "<Mouse>/scroll", "<Mouse>/scroll/y",
            "<Mouse>/delta/x", "<Keyboard>/anyKey", "<Keyboard>/imeSelected", "<Keyboard>/", "<Keyboard>/*", "<Keyboard>/a/b",
            "<Gamepad>/leftStick", "<Pointer>/position", "2DVector", "ButtonWithOneModifier" })
        {
            Check(!GameBindingRules.Editable(path, false), "nonbutton, missing or composite path refused: " + path);
            Check(!GameBindingRules.Overlaps(path, path), "invalid path does not become a shared-control warning: " + path);
        }
        foreach (var pair in new[] { ("ctrl", "leftCtrl"), ("ctrl", "rightControl"), ("control", "leftCtrl"), ("shift", "leftShift"),
            ("shift", "rightShift"), ("alt", "leftAlt"), ("alt", "rightAlt"), ("meta", "leftWindows"), ("meta", "rightCommand") })
        {
            Check(GameBindingRules.Overlaps("<Keyboard>/" + pair.Item1, "<Keyboard>/" + pair.Item2), "aggregate modifier overlaps its physical side");
            Check(GameBindingRules.Overlaps("<Keyboard>/" + pair.Item2, "<Keyboard>/" + pair.Item1), "overlap is symmetric");
        }
        foreach (var pair in new[] { ("<Keyboard>/leftCtrl", "<Keyboard>/rightCtrl"), ("<Keyboard>/a", "<Keyboard>/b"),
            ("<Mouse>/leftButton", "<Mouse>/rightButton"), ("<Keyboard>/shift", "<Mouse>/rightButton"), ("<Keyboard>/a", "<Mouse>/backButton") })
            Check(!GameBindingRules.Overlaps(pair.Item1, pair.Item2), "different physical buttons remain distinct");
        Console.WriteLine($"GAME_BINDING_RULES_TESTS_OK {checks}");
    }
}
