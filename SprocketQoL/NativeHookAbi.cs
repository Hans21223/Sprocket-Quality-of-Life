namespace SprocketQoL;

/// Windows x64 passes only these aggregate sizes inline. Harmony's generated
/// boxed-value conversion instead expects an address to the aggregate's data.
internal static class NativeHookAbi
{
    internal static bool UnsafeBoxedArgument(int pointerBytes, bool byReference, int nativeBytes)
        => pointerBytes == 8 && !byReference && nativeBytes is 1 or 2 or 4 or 8;

    internal static bool UnsafeBoxedReturn(int pointerBytes, int nativeBytes)
        => pointerBytes == 8 && nativeBytes is 1 or 2 or 4 or 8;

    /// The game method a hook written for `name(start...)` takes when that exact form is gone: the one that starts with
    /// the same arguments and only adds more after them. Experimental (2026-11) added (onExecuted, mode) to
    /// Execute(op, options, group) and dropped the short form; hooks reading only the first arguments still fit it.
    internal static System.Reflection.MethodInfo? LongerForm(IEnumerable<System.Reflection.MethodInfo> methods, string name, Type[]? start) =>
        start == null ? null : methods.FirstOrDefault(m => m.Name == name && m.GetParameters() is var p && p.Length > start.Length
            && p.Take(start.Length).Select(x => x.ParameterType).SequenceEqual(start));
}
