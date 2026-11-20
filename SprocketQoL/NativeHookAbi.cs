namespace SprocketQoL;

/// Windows x64 passes only these aggregate sizes inline. Harmony's generated
/// boxed-value conversion instead expects an address to the aggregate's data.
internal static class NativeHookAbi
{
    internal static bool UnsafeBoxedArgument(int pointerBytes, bool byReference, int nativeBytes)
        => pointerBytes == 8 && !byReference && nativeBytes is 1 or 2 or 4 or 8;
}
