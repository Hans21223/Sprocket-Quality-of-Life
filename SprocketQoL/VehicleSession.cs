namespace SprocketQoL;

/// Which vehicle QoL's design edits belong to. An edit asked for on one vehicle must never run on another one, opened
/// while it waited for the editor, nor hand that one its Restore button or follow-up step. The editor's vehicle pointer
/// can come back for a different vehicle, so every change of vehicle except QoL's own reload starts a new session.
internal sealed class VehicleSession
{
    IntPtr seen;
    bool ownLoad;

    internal int Now { get; private set; }

    /// Each frame the editor runs: the vehicle in it (zero while there's none).
    internal void Saw(IntPtr target)
    {
        if (target == IntPtr.Zero || target == seen) return;
        if (seen != IntPtr.Zero && !ownLoad) Now++;
        ownLoad = false; // QoL's reload makes one new vehicle; any after it are someone else's
        seen = target;
    }

    /// The editor stopped running. During QoL's own reload that's the reload, not the player leaving.
    internal void Left()
    {
        if (ownLoad) return;
        Now++;
        seen = IntPtr.Zero;
    }

    /// QoL is about to load a design over the current vehicle (the next new one is its result), and when that's done.
    internal void Loading() => ownLoad = true;
    internal void Loaded() => ownLoad = false;
}
