namespace SprocketQoL;

/// Raw shortcuts wait while a QoL window owns the user's input. Native UI callbacks remain available.
internal static class EditorModal
{
    internal static bool BlocksInput => QolMenu.IsOpen || WorkshopLibrary.BlocksInput || CommandPalette.BlocksInput
        || PartPatterns.BlocksInput;
}
