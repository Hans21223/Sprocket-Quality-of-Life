using Il2CppInterop.Runtime;
using Sprocket.UI;
using UnityEngine.Events;

namespace SprocketQoL;

internal static class Ui
{
    // Tools depend on Panel and InspectorUi, never the game's panel types: the panel is the game's own (NativePanels) or
    // QoL's fallback (QolPanel).
    static IInspectorBackend? latestBackend;
    internal static InspectorUi? Drawer(Panel? panel) => panel == null ? null : new(panel.Backend);
    internal static UiPresentation.ScreenBox? InspectorBounds()
    {
        try { return latestBackend?.ScreenBounds; }
        catch { latestBackend = null; return null; } // a replaced native UI can use the screen-contained fallback
    }
    internal static void LeftEditor() => latestBackend = null;
    /// The mouse is over the game's UI, by the game's own every-frame flag. Hit tests use this: the inspector's measured
    /// bounds can span the whole screen (its scroll view does in 0.2.56), which hid every hover mark.
    internal static bool PointerOverGameUi() => Sprocket.Inputsystem.Inputs.PointerOverGUI;
    /// Several native component editors share one inspector layout (e.g. ring and basket).
    /// A QoL postfix must close its final foldout before the next native editor draws.
    internal static void Inspector(string feature, Panel? layout, Action draw) => Guard(feature, () =>
    {
        var backend = layout?.Backend;
        if (backend == null) return;
        latestBackend = backend;
        try { draw(); }
        finally { backend.EndSections(); }
    });

    /// An exception thrown back into the game's inspector drawing could take the game down; log it instead.
    internal static void Guard(string feature, Action draw)
    {
        if (missing.Contains(feature)) return;
        try { draw(); }
        catch (Exception ex) when (ex is FileNotFoundException or TypeLoadException or MissingMemberException)
        {
            // Code this Sprocket version doesn't have (QoL built for another version): it never works, so it stops trying
            // (Speed & acceleration on 0.2.55.5 failed every frame: Sprocket.EngineDesigner.Editor arrived in 0.2.56).
            missing.Add(feature);
            Plugin.ModLog.LogWarning($"{feature} is off: this Sprocket version doesn't have what it needs ({ex.GetType().Name}: {ex.Message})");
        }
        catch (Exception ex)
        {
            // The same error again (a GUI drawn every frame) is counted, not logged again: no wall of red.
            string text = ex.ToString();
            if (lastErrors.TryGetValue(feature, out var last) && last.Text == text)
            {
                lastErrors[feature] = (text, ++last.Count);
                // Name the error too: the full one scrolled away long ago, and a screenshot only shows this line.
                if (last.Count % 1000 == 0) Plugin.ModLog.LogError($"{feature}: the same error again, {last.Count} times so far ({ex.GetType().Name}: {ex.Message}; full error at the first time in LogOutput.log)");
                return;
            }
            lastErrors[feature] = (text, 1);
            Plugin.ModLog.LogError($"{feature}: {ex}");
        }
    }

    static readonly Dictionary<string, (string Text, int Count)> lastErrors = new();
    static readonly HashSet<string> missing = new();

    internal static UnityAction Callback(Action action) => DelegateSupport.ConvertDelegate<UnityAction>(action)!;

    internal static Il2CppSystem.Action<float> FloatCallback(Action<float> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<float>>(action)!;

    internal static Il2CppSystem.Action<bool> BoolCallback(Action<bool> action) =>
        DelegateSupport.ConvertDelegate<Il2CppSystem.Action<bool>>(action)!;

    static UiPresentation.FoldState? closed;
    static string? foldedValue;

    /// Starts a top-level section the player can fold away (the game's own dropdown). It stays folded, across restarts
    /// too (saved in the plugin's config file).
    internal static void Section(Panel? layout, string title)
    {
        var backend = layout?.Backend;
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
