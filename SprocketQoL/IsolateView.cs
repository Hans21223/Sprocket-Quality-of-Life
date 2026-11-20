using Il2CppInterop.Runtime;
using Sprocket.Vehicles;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketQoL;

/// Show only the selected parts (Blender's local view): work on a hull without the tracks and turret in the way. The
/// other parts' colliders go too, so clicks don't land on what can't be seen. The key again, or leaving the editor,
/// shows everything as it was.
internal static class IsolateView
{
    static readonly List<Renderer> renderers = new();
    static readonly List<Collider> colliders = new();
    static readonly List<DecalProjector> projectors = new();
    static readonly HashSet<IntPtr> hidden = new();
    static HashSet<int> keep = new();
    static IntPtr target;
    static int session;
    static float next;
    internal static bool Active => target != IntPtr.Zero;

    internal static void Keys()
    {
        if (Keybinds.Pressed("isolate") && !MeshTools.Typing()) Toggle();
    }

    internal static void Toggle()
    {
        var editor = DesignEditor.Instance;
        if (editor == null || editor.CaptureBlocked()) return;
        if (Active) { Show(); return; }
        if (!editor.IsReady || editor.IsBusy || editor.Core?.Editor?.OperationInProgress == true) return;
        keep = editor.SelectedParts().ToHashSet();
        if (keep.Count == 0) { editor.Say("Select the parts to keep in view first.", 4); return; }
        target = editor.Core?.Target?.Pointer ?? IntPtr.Zero;
        if (target == IntPtr.Zero) return;
        session = editor.Session;
        try { HideOthers(editor); }
        catch { Show(); throw; }
        if (hidden.Count == 0) { Show(); editor.Say("Nothing else to hide.", 3); return; }
        next = Time.unscaledTime + .5f;
        editor.Say($"Showing only the selected parts. {Keybinds.Shown("isolate")} again shows the rest.", 5);
    }

    internal static void Tick()
    {
        if (!Active) return;
        var editor = DesignEditor.Instance;
        if (editor?.IsReady != true || editor.Session != session || editor.Core?.Target?.Pointer != target) { Show(); return; }
        if (Time.unscaledTime < next || editor.IsBusy || PhotoShot.Capturing || DrawingSheet.Capturing) return;
        next = Time.unscaledTime + .5f;
        // Mesh edits can rebuild renderers/colliders without replacing the vehicle.
        // Newly built hidden parts must remain unpickable until isolation ends.
        try { HideOthers(editor); }
        catch { Show(); throw; }
    }

    static void HideOthers(DesignEditor editor)
    {
        var design = editor.AllParts().Select(p => p.Pointer).ToHashSet();
        // A part's own renderers, colliders and decals: those whose nearest part is it (a part's children are parts too).
        bool Hide(Component c) => c.GetComponentInParent<VehicleObject>() is { } part && design.Contains(part.Pointer) && !keep.Contains((int)part.VUID);
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
            if (o.TryCast<Renderer>() is { enabled: true } r && Hide(r)) { if (hidden.Add(r.Pointer)) renderers.Add(r); r.enabled = false; }
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Collider>()))
            if (o.TryCast<Collider>() is { enabled: true } c && Hide(c)) { if (hidden.Add(c.Pointer)) colliders.Add(c); c.enabled = false; }
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DecalProjector>()))
            if (o.TryCast<DecalProjector>() is { enabled: true } d && Hide(d)) { if (hidden.Add(d.Pointer)) projectors.Add(d); d.enabled = false; }
    }

    /// Everything hidden back on (parts the game has since rebuilt are new objects, shown already).
    internal static void Show()
    {
        // A destroyed component must not prevent restoring the other parts.
        foreach (var r in renderers) try { if (r != null) r.enabled = true; } catch { }
        foreach (var c in colliders) try { if (c != null) c.enabled = true; } catch { }
        foreach (var d in projectors) try { if (d != null) d.enabled = true; } catch { }
        renderers.Clear(); colliders.Clear(); projectors.Clear();
        hidden.Clear(); keep.Clear(); target = IntPtr.Zero; next = 0;
    }
}
