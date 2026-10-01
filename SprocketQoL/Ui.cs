using Il2CppInterop.Runtime;
using Sprocket.UI;
using UnityEngine.Events;

namespace SprocketQoL;

internal static class Ui
{
    // Replace this factory when a new inspector implementation is supported.
    // Tools depend on InspectorUi, not the native drawer's concrete layout.
    internal static Func<IGUILayout, IInspectorBackend?> BackendFactory = NativeInspectorBackend.Create;
    static IInspectorBackend? latestBackend;
    internal static InspectorUi? Drawer(IGUILayout layout) => BackendFactory(layout) is { } backend ? new(backend) : null;
    internal static UiPresentation.ScreenBox? InspectorBounds()
    {
        try { return latestBackend?.ScreenBounds; }
        catch { latestBackend = null; return null; } // a replaced native UI can use the screen-contained fallback
    }
    internal static void LeftEditor() => latestBackend = null;
    /// Several native component editors share one inspector layout (e.g. ring and basket).
    /// A QoL postfix must close its final foldout before the next native editor draws.
    internal static void Inspector(string feature, IGUILayout layout, Action draw) => Guard(feature, () =>
    {
        var backend = BackendFactory(layout);
        if (backend == null) return;
        latestBackend = backend;
        try { draw(); }
        finally { backend.EndSections(); }
    });

    /// An exception thrown back into the game's inspector drawing could take the game down; log it instead.
    internal static void Guard(string feature, Action draw)
    {
        try { draw(); }
        catch (Exception ex)
        {
            // The same error again (a GUI drawn every frame) is counted, not logged again: no wall of red.
            string text = ex.ToString();
            if (lastErrors.TryGetValue(feature, out var last) && last.Text == text)
            {
                lastErrors[feature] = (text, ++last.Count);
                if (last.Count % 1000 == 0) Plugin.ModLog.LogError($"{feature}: the same error again, {last.Count} times so far");
                return;
            }
            lastErrors[feature] = (text, 1);
            Plugin.ModLog.LogError($"{feature}: {ex}");
        }
    }

    static readonly Dictionary<string, (string Text, int Count)> lastErrors = new();

    internal static UnityAction Callback(Action action) => DelegateSupport.ConvertDelegate<UnityAction>(action)!;

    internal static Il2CppSystem.Action<float> FloatCallback(Action<float> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(action)!;

    internal static Il2CppSystem.Action<bool> BoolCallback(Action<bool> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(action)!;

    static UiPresentation.FoldState? closed;
    static string? foldedValue;

    /// Starts a top-level section the player can fold away (the game's own dropdown). It stays folded, across restarts
    /// too (saved in the plugin's config file).
    internal static void Section(IGUILayout layout, string title)
    {
        var backend = BackendFactory(layout);
        if (backend == null) return;
        string saved = Plugin.Folded?.Value ?? "";
        if (closed == null || saved != foldedValue) { closed = new(saved); foldedValue = saved; }
        var section = UiPresentation.Resolve(title);
        backend.EndSections();
        backend.BeginSection(section.Title, closed.IsOpen(section.Id), BoolCallback(open =>
        {
            // A delayed UI callback must preserve changes made by another inspector.
            var state = new UiPresentation.FoldState(Plugin.Folded?.Value);
            if (!state.SetOpen(section.Id, open)) return;
            closed = state; foldedValue = state.Serialize();
            if (Plugin.Folded != null) Plugin.Folded.Value = foldedValue;
        }));
    }
}
