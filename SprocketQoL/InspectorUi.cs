using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.UI;
using UnityEngine;
using UnityEngine.Events;

namespace SprocketQoL;

/// A small boundary between tools and the game's current inspector widgets.
/// Future UI backends can implement this contract without changing tool operations.
internal interface IInspectorBackend
{
    float FieldWidth { get; }
    UiPresentation.ScreenBox? ScreenBounds { get; }
    void Info(string text, int lines);
    void Button(string label, UnityAction onClick, ref UITooltip tooltip);
    void Slider(string label, float value, float min, float max, Il2CppSystem.Action<float> change);
    void Toggle(string label, bool value, Il2CppSystem.Action<bool> change, string tooltip);
    void BeginSection(string title, bool expanded, Il2CppSystem.Action<bool> change);
    void EndSections();
}

internal sealed class InspectorUi
{
    readonly IInspectorBackend backend;
    internal InspectorUi(IInspectorBackend backend) => this.backend = backend;
    internal void InfoField(string text, int lines = 1) => backend.Info(text, UiPresentation.InfoLines(text, backend.FieldWidth, lines));
    internal void Button(string label, UnityAction onClick, ref UITooltip tooltip) => backend.Button(label, onClick, ref tooltip);
    internal void Slider(string label, float value, float min, float max, Il2CppSystem.Action<float> change) => backend.Slider(label, value, min, max, change);
    internal void Slider(string label, int value, int min, int max, Il2CppSystem.Action<float> change) => backend.Slider(label, value, min, max, change);
    internal void ToggleField(string label, bool value, Il2CppSystem.Action<bool> change, string tooltip = "") => backend.Toggle(label, value, change, tooltip);
}

/// All native casts, width reads and widget/layout calls live here.
internal sealed class NativeInspectorBackend : IInspectorBackend
{
    readonly IGUILayout layout;
    readonly IGUIElementDrawer drawer;
    Il2CppStructArray<Vector3>? corners;
    NativeInspectorBackend(IGUILayout layout, IGUIElementDrawer drawer) { this.layout = layout; this.drawer = drawer; }
    internal static IInspectorBackend? Create(IGUILayout layout) => layout.TryCast<IGUIElementDrawer>() is { } drawer ? new NativeInspectorBackend(layout, drawer) : null;
    public float FieldWidth => drawer.ElementWidth;
    public UiPresentation.ScreenBox? ScreenBounds
    {
        get
        {
            var component = layout.TryCast<Component>();
            if (component == null) return null;
            var scroll = component.GetComponentInParent<UnityEngine.UI.ScrollRect>();
            var rect = scroll?.viewport ?? component.transform.TryCast<RectTransform>();
            if (rect == null || !rect.gameObject.activeInHierarchy) return null;
            var canvas = rect.GetComponentInParent<Canvas>();
            var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            corners ??= new(4);
            rect.GetWorldCorners(corners);
            float left = float.PositiveInfinity, right = float.NegativeInfinity;
            float top = float.PositiveInfinity, bottom = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                var point = RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
                left = Math.Min(left, point.x); right = Math.Max(right, point.x);
                top = Math.Min(top, Screen.height - point.y); bottom = Math.Max(bottom, Screen.height - point.y);
            }
            if (!float.IsFinite(left) || !float.IsFinite(top) || !float.IsFinite(right) || !float.IsFinite(bottom) || right <= left || bottom <= top) return null;
            return new(left, top, right - left, bottom - top);
        }
    }
    public void Info(string text, int lines) => drawer.InfoField(text, lines);
    public void Button(string label, UnityAction onClick, ref UITooltip tooltip) => drawer.Button(label, onClick, ref tooltip);
    public void Slider(string label, float value, float min, float max, Il2CppSystem.Action<float> change) => drawer.Slider(label, value, min, max, change);
    public void Toggle(string label, bool value, Il2CppSystem.Action<bool> change, string tooltip) => drawer.ToggleField(label, value, change, tooltip);
    public void BeginSection(string title, bool expanded, Il2CppSystem.Action<bool> change) => layout.BeginDropdown(title, expanded, change);
    public void EndSections() => layout.EndAllDropdowns();
}
