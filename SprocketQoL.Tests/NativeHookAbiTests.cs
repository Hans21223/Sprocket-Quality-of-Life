using SprocketQoL;

internal static class NativeHookAbiTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        // Win64 aggregate ABI: a small boxed native struct arrives as data, not
        // a data address. CancellationToken is exactly one eight-byte word.
        foreach (int bytes in new[] { 1, 2, 4, 8 })
            Check(NativeHookAbi.UnsafeBoxedArgument(8, false, bytes), "inline boxed structs cannot use the generic Harmony conversion");
        foreach (int bytes in new[] { 1, 2, 4, 8 })
            Check(NativeHookAbi.UnsafeBoxedReturn(8, bytes), "inline boxed struct returns cannot use the generic Harmony conversion");
        foreach (int bytes in new[] { 3, 5, 6, 7, 9, 12, 16, 24, 32 })
            Check(!NativeHookAbi.UnsafeBoxedArgument(8, false, bytes), "other native aggregate sizes already arrive through addressable storage");
        foreach (int bytes in new[] { 3, 5, 6, 7, 9, 12, 16, 24, 32 })
            Check(!NativeHookAbi.UnsafeBoxedReturn(8, bytes), "other native aggregate returns already use addressable storage");
        foreach (int bytes in new[] { 1, 2, 4, 8, 16 })
            Check(!NativeHookAbi.UnsafeBoxedArgument(8, true, bytes), "explicit native references are data addresses, not inline values");
        Check(!NativeHookAbi.UnsafeBoxedArgument(4, false, 8), "the Win64 guard does not claim to validate the x86 ABI");
        Check(!NativeHookAbi.UnsafeBoxedReturn(4, 8), "the return guard does not claim to validate the x86 ABI");

        // A hook written for Execute(op, options, group) on a game whose Execute also takes (onExecuted, mode), like
        // experimental's, and whose other Execute has three arguments of other types: it takes the longer form.
        var forms = typeof(GameForms).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly);
        var start = new[] { typeof(string), typeof(bool), typeof(int) };
        Check(NativeHookAbi.LongerForm(forms, "Execute", start) is { Name: "Execute" } taken && taken.GetParameters().Length == 5,
              "a hook on Execute(string, bool, int) takes Execute(string, bool, int, Action, long), not Run with the same start");
        Check(NativeHookAbi.LongerForm(forms, "Execute", new[] { typeof(bool), typeof(int) }) == null, "a form that starts differently isn't taken");
        Check(NativeHookAbi.LongerForm(forms, "Gone", start) == null && NativeHookAbi.LongerForm(forms, "Execute", null) == null, "a name the game hasn't got, or a hook naming no arguments, finds nothing");
        Console.WriteLine($"NATIVE_HOOK_ABI_TESTS_OK: {checks} checks passed");
    }

    static class GameForms
    {
        static object? Execute(string op, bool options, int group, Action? onExecuted, long mode) => null;
        static void Execute(double edit, int group, long mode) { }
        static void Run(string op, bool options, int group, Action? onExecuted) { }
    }
}
