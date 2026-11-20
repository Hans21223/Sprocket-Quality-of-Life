using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace SprocketQoL;

/// UI scale (Mod Options): every canvas the game sizes with the screen (its panels, menus, labels; QoL's windows too)
/// drawn bigger or smaller. Each canvas's own size is kept and put back at 100%; one the game sets again starts from
/// the game's new size.
internal static class UiScale
{
    sealed class Scaled
    {
        public CanvasScaler Scaler = null!;
        public readonly CanvasScaleState State = new();
        public RectTransform? Panel;
        public Vector2 PanelSize;
    }
    static readonly Dictionary<IntPtr, Scaled> canvases = new();
    static float applied = 1, next;

    internal static void Tick()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 0.5f;
        float percent = Plugin.UiScalePercent?.Value ?? 100;
        float scale = CanvasScaleState.Scale(percent);
        if (scale == 1 && applied == 1) return;
        foreach (var gone in canvases.Where(c => c.Value.Scaler == null).Select(c => c.Key).ToList()) canvases.Remove(gone);
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<CanvasScaler>()))
        {
            if (o.TryCast<CanvasScaler>() is not { } s) continue;
            if (!canvases.TryGetValue(s.Pointer, out var c)) canvases[s.Pointer] = c = new Scaled { Scaler = s };
            Apply(c, percent);
        }
        applied = scale;
        if (scale == 1) canvases.Clear();
    }

    static void Apply(Scaled c, float percent)
    {
        var s = c.Scaler;
        var mode = s.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize ? CanvasScaleState.Mode.ScreenSize
            : s.uiScaleMode == CanvasScaler.ScaleMode.ConstantPixelSize ? CanvasScaleState.Mode.ConstantPixels : CanvasScaleState.Mode.Physical;
        var reference = s.referenceResolution;
        string name = s.gameObject.name;
        bool ours = name.StartsWith("QoL ", StringComparison.Ordinal) || name == "Part pattern preview";
        // NativeUi's complete windows use fixed pixel layouts inside 1100 x 760.
        // Keep that space available instead of cropping their controls at 200%.
        var minimum = ours && name != "QoL panel" ? new System.Numerics.Vector2(1100, 760) : default;
        var plan = c.State.Apply(mode, new(reference.x, reference.y), s.scaleFactor, percent, minimum);
        // scaleFactor in ScreenSize mode is a calculated output of CanvasScaler.Update.
        // Writing or using it to rebase would magnify the UI again every half-second.
        if (plan.SetReference) s.referenceResolution = new(plan.Reference.X, plan.Reference.Y);
        if (plan.SetFactor) s.scaleFactor = plan.Factor;
        if (name == "QoL panel")
        {
            if (CanvasScaleState.Scale(percent) != 1) FitPanel(c);
            else if (c.Panel != null) c.Panel.sizeDelta = c.PanelSize;
        }
    }

    static void FitPanel(Scaled c)
    {
        if (c.Scaler.transform.TryCast<RectTransform>() is not { } root) return;
        if (c.Panel == null && root.Find("Frame")?.TryCast<RectTransform>() is { } panel)
        { c.Panel = panel; c.PanelSize = panel.sizeDelta; }
        if (c.Panel == null) return;
        float width = root.rect.width, height = root.rect.height;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width < 144 || height < 120) return;
        var size = new Vector2(Math.Min(c.PanelSize.x, width - 24), Math.Min(c.PanelSize.y, height - 24));
        c.Panel.sizeDelta = size; // its existing scroll view fits the shorter frame
        var at = c.Panel.anchoredPosition;
        float x = c.Panel.anchorMin.x == 1 ? Math.Clamp(at.x, -width + size.x + 12, -12)
            : Math.Clamp(at.x, 12, width - size.x - 12);
        c.Panel.anchoredPosition = new(x, -Math.Clamp(-at.y, 12, height - size.y - 12));
    }

    internal static void Restore()
    {
        foreach (var c in canvases.Values) try
        {
            if (c.Scaler != null) Apply(c, 100);
            if (c.Panel != null) c.Panel.sizeDelta = c.PanelSize;
        }
        catch { }
        canvases.Clear(); applied = 1; next = 0;
    }
}
