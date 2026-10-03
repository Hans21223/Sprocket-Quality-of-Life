using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace SprocketQoL;

/// QoL's own part panel, for when the game's can't be drawn into (a game update changed its panels): every QoL section
/// of the selected parts, in a panel on the right made only of Unity's own UI parts. Automatic (the default) shows it
/// when the game's panel hooks didn't attach, or the game never draws its part panels; Always shows it beside the
/// game's; Off never. Rebuilt when the selection changes, after a click, and once a second (for figures that change).
internal static class QolPanel
{
    internal static readonly string[] Modes = { "Automatic", "Always", "Off" };
    static GameObject? canvas;
    static RectTransform? content, frame, head;
    static Canvas? canvasComponent;
    static readonly InputShield.Drag drag = new();
    static TextMeshProUGUI? title;
    static TMP_FontAsset? font;
    static readonly List<Il2CppObjectBase> kept = new(); // callbacks the panel's widgets hold
    static string shown = "";
    static bool dirty;
    static float nextRefresh, openSince = -1;

    internal static string Mode => Plugin.PanelMode?.Value is { } m && Modes.Contains(m) ? m : "Automatic";

    /// The game's part panel hooks that didn't attach (NativePanels).
    static bool NativeMissing => Hooks.Report.Any(e => e.Feature == nameof(NativePanels) && e.Error != null);

    /// Why the panel shows now, or null.
    static string? Reason(bool partOpen)
    {
        if (Mode == "Off" || !partOpen) { openSince = -1; return null; }
        if (Mode == "Always") return "always shown (Mod Options)";
        if (NativeMissing) return "the game's part panel couldn't be hooked";
        if (openSince < 0) openSince = Time.unscaledTime;
        // Hooked, but the game never draws its part panels (they moved elsewhere): after 10 s with a part open.
        return NativePanels.LastDrawn < 0 && Time.unscaledTime - openSince > 10 ? "the game's part panel isn't drawn" : null;
    }

    /// Every frame in the editor.
    internal static void Update()
    {
        var editors = PanelSections.ActiveEditors().Where(PanelSections.Has).ToList();
        var reason = Reason(editors.Count > 0);
        if (reason == null) { Close(); return; }
        string key = string.Join(",", editors.Select(e => e.Pointer));
        bool holding = Mouse.current?.leftButton.isPressed == true; // not while a slider is dragged or a button pressed
        if (canvas == null || key != shown || (!holding && (dirty || Time.unscaledTime >= nextRefresh))) Build(editors, key, reason);
        Move();
    }

    /// Over the panel the game's mouse controls wait; its title bar drags it (remembered, in the panel's own units).
    static void Move()
    {
        if (frame == null || head == null || canvasComponent == null || Mouse.current is not { } mouse) return;
        var box = OnScreen(frame);
        var at = mouse.position.ReadValue();
        if (drag.Active || box.Contains(new Vector2(at.x, Screen.height - at.y))) InputShield.Claim();
        float scale = Math.Max(0.01f, canvasComponent.scaleFactor);
        if (drag.Update(OnScreen(head), box.position, done => { if (Plugin.PanelPosition != null) Plugin.PanelPosition.Value = InputShield.Write(done / scale); }) is { } to)
            Place(to / scale);
    }

    /// A rectangle's place on screen, in pixels from the top left.
    static UnityEngine.Rect OnScreen(RectTransform r)
    {
        var corners = new Il2CppStructArray<Vector3>(4);
        r.GetWorldCorners(corners);
        return new UnityEngine.Rect(corners[0].x, Screen.height - corners[1].y, corners[2].x - corners[0].x, corners[1].y - corners[0].y);
    }

    /// The panel's top left at `at` (panel units from the screen's top left), kept on screen.
    static void Place(Vector2 at)
    {
        if (frame == null || canvasComponent == null) return;
        float scale = Math.Max(0.01f, canvasComponent.scaleFactor), wide = Screen.width / scale, high = Screen.height / scale;
        frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(0, 1);
        frame.anchoredPosition = new Vector2(Math.Clamp(at.x, 0, Math.Max(0, wide - 120)), -Math.Clamp(at.y, 0, Math.Max(0, high - 40)));
    }

    internal static void Close()
    {
        if (canvas != null) { canvas.SetActive(false); UnityEngine.Object.Destroy(canvas); }
        canvas = null; content = frame = head = null; canvasComponent = null; title = null; kept.Clear(); shown = "";
    }

    static void Build(List<Il2CppObjectBase> editors, string key, string reason)
    {
        shown = key; dirty = false; nextRefresh = Time.unscaledTime + 1;
        if (canvas == null) Open();
        if (content == null || title == null) return;
        title.text = $"Quality of Life  ·  {reason}  (drag here to move)";
        for (int i = content.childCount - 1; i >= 0; i--) { var c = content.GetChild(i).gameObject; c.SetActive(false); UnityEngine.Object.Destroy(c); }
        kept.Clear();
        var backend = new Backend(content);
        var panel = new Panel(backend, () => dirty = true);
        foreach (var editor in editors) PanelSections.Draw(editor, panel);
        if (content.childCount == 0) backend.Info("Nothing from Quality of Life for the selected parts.", 1);
    }

    static void Open()
    {
        font = NativeUi.Font();
        canvas = new GameObject("QoL panel", new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
        var c = canvasComponent = canvas.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = 29000;
        var scaler = canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvas.AddComponent<GraphicRaycaster>();
        // The frame: on the right, below the editor's top bar.
        frame = Make("Frame", canvas.transform);
        frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(1, 1);
        frame.anchoredPosition = new Vector2(-12, -96);
        frame.sizeDelta = new Vector2(420, 1080 - 96 - 120);
        if (InputShield.Read(Plugin.PanelPosition?.Value) is { } saved) Place(saved); // where it was dragged to
        frame.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.075f, 0.08f, 0.94f);
        head = Make("Title", frame);
        head.anchorMin = new Vector2(0, 1); head.anchorMax = new Vector2(1, 1); head.pivot = new Vector2(0.5f, 1);
        head.anchoredPosition = new Vector2(0, -6); head.sizeDelta = new Vector2(-20, 26);
        title = Text(head, "", 15, new Color(0.95f, 0.72f, 0.35f));
        // The scrolling list.
        var view = Make("Viewport", frame);
        view.anchorMin = Vector2.zero; view.anchorMax = Vector2.one; view.pivot = new Vector2(0.5f, 1);
        view.offsetMin = new Vector2(8, 8); view.offsetMax = new Vector2(-8, -36);
        view.gameObject.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f); // takes the wheel and clicks
        view.gameObject.AddComponent<RectMask2D>();
        content = Make("Content", view);
        content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(0.5f, 1);
        content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
        var list = content.gameObject.AddComponent<VerticalLayoutGroup>();
        list.spacing = 4; list.childControlHeight = true; list.childControlWidth = true;
        list.childForceExpandHeight = false; list.childForceExpandWidth = true;
        content.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = frame.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = view; scroll.content = content; scroll.horizontal = false; scroll.scrollSensitivity = 30;
        scroll.movementType = ScrollRect.MovementType.Clamped;
    }

    static RectTransform Make(string name, Transform parent)
    {
        var go = new GameObject(name, new Il2CppReferenceArray<Il2CppSystem.Type>(new[] { Il2CppType.Of<RectTransform>() }));
        var r = go.GetComponent<RectTransform>(); r.SetParent(parent, false);
        return r;
    }

    static void Stretch(RectTransform r, float left = 0, float right = 0)
    {
        r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(left, 0); r.offsetMax = new Vector2(-right, 0);
    }

    static TextMeshProUGUI Text(RectTransform r, string text, float size, Color colour)
    {
        var t = r.gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font; t.fontSize = size; t.color = colour; t.richText = false; t.raycastTarget = false;
        t.enableWordWrapping = true; t.text = text; t.alignment = TextAlignmentOptions.MidlineLeft;
        return t;
    }

    static T Keep<T>(T callback) where T : Il2CppObjectBase { kept.Add(callback); return callback; }

    /// The panel's rows, drawn the way the game's part panel draws them: info text, buttons, sliders, toggles, and
    /// sections that fold (QoL's fold state, the same as in the game's panel).
    sealed class Backend : IInspectorBackend
    {
        readonly RectTransform list;
        bool folded;
        internal Backend(RectTransform list) { this.list = list; }

        public float FieldWidth => 380;

        public UiPresentation.ScreenBox? ScreenBounds
        {
            get
            {
                if (frame == null) return null;
                var corners = new Il2CppStructArray<Vector3>(4);
                frame.GetWorldCorners(corners);
                float left = corners[0].x, right = corners[2].x, top = Screen.height - corners[1].y, bottom = Screen.height - corners[0].y;
                return right > left && bottom > top ? new(left, top, right - left, bottom - top) : null;
            }
        }

        RectTransform Row(string name, float height)
        {
            var r = Make(name, list);
            var e = r.gameObject.AddComponent<LayoutElement>();
            e.minHeight = height; e.preferredHeight = height;
            return r;
        }

        public void Info(string text, int lines)
        {
            if (folded) return;
            var r = Row("Info", Math.Max(1, lines) * 19 + 4);
            Text(r, text, 14, new Color(0.82f, 0.83f, 0.8f)).alignment = TextAlignmentOptions.TopLeft;
        }

        public void Button(string label, UnityAction onClick, Tip tip)
        {
            if (folded) return;
            var r = Row("Button " + label, 30);
            var image = r.gameObject.AddComponent<Image>();
            image.color = new Color(0.2f, 0.21f, 0.23f);
            var button = r.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            button.onClick.AddListener(Keep(Ui.Callback(() => dirty = true)));
            var t = Make("Label", r); Stretch(t, 10, 10);
            Text(t, label, 15, new Color(0.93f, 0.93f, 0.92f)).alignment = TextAlignmentOptions.Center;
        }

        public void Slider(string label, float value, float min, float max, Il2CppSystem.Action<float> change)
        {
            if (folded) return;
            var r = Row("Slider " + label, 42);
            var name = Make("Label", r);
            name.anchorMin = new Vector2(0, 1); name.anchorMax = new Vector2(1, 1); name.pivot = new Vector2(0.5f, 1);
            name.anchoredPosition = Vector2.zero; name.sizeDelta = new Vector2(0, 20);
            var shownLabel = Text(name, $"{label}: {value:0.##}", 14, new Color(0.82f, 0.83f, 0.8f));
            var bar = Make("Bar", r);
            bar.anchorMin = new Vector2(0, 0); bar.anchorMax = new Vector2(1, 0); bar.pivot = new Vector2(0.5f, 0);
            bar.anchoredPosition = new Vector2(0, 4); bar.sizeDelta = new Vector2(-16, 14);
            bar.gameObject.AddComponent<Image>().color = new Color(0.16f, 0.17f, 0.19f);
            var fillArea = Make("Fill Area", bar); Stretch(fillArea);
            var fill = Make("Fill", fillArea); Stretch(fill);
            fill.gameObject.AddComponent<Image>().color = new Color(0.85f, 0.6f, 0.25f, 0.8f);
            var handleArea = Make("Handle Area", bar); Stretch(handleArea);
            var handle = Make("Handle", handleArea);
            handle.sizeDelta = new Vector2(14, 0);
            var knob = handle.gameObject.AddComponent<Image>();
            knob.color = new Color(0.95f, 0.93f, 0.88f);
            var slider = bar.gameObject.AddComponent<Slider>();
            slider.fillRect = fill; slider.handleRect = handle; slider.targetGraphic = knob;
            slider.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            slider.minValue = min; slider.maxValue = max;
            slider.SetValueWithoutNotify(Math.Clamp(value, min, max));
            slider.onValueChanged.AddListener(Keep(DelegateSupport.ConvertDelegate<UnityAction<float>>(new Action<float>(v =>
            {
                change.Invoke(v);
                shownLabel.text = $"{label}: {v:0.##}";
                dirty = true; // rebuilt when the mouse lets go
            }))!));
        }

        public void Toggle(string label, bool value, Il2CppSystem.Action<bool> change, string tooltip)
        {
            if (folded) return;
            var r = Row("Toggle " + label, 26);
            var box = Make("Box", r);
            box.anchorMin = box.anchorMax = new Vector2(0, 0.5f); box.pivot = new Vector2(0, 0.5f);
            box.anchoredPosition = new Vector2(2, 0); box.sizeDelta = new Vector2(20, 20);
            var boxImage = box.gameObject.AddComponent<Image>();
            boxImage.color = new Color(0.2f, 0.21f, 0.23f);
            var mark = Make("Check", box); Stretch(mark, 4, 4); mark.offsetMin = new Vector2(4, 4); mark.offsetMax = new Vector2(-4, -4);
            var markImage = mark.gameObject.AddComponent<Image>();
            markImage.color = new Color(0.95f, 0.7f, 0.3f);
            var t = Make("Label", r); Stretch(t, 30, 0);
            Text(t, label, 14, new Color(0.9f, 0.9f, 0.88f));
            var toggle = r.gameObject.AddComponent<UnityEngine.UI.Toggle>();
            toggle.targetGraphic = boxImage; toggle.graphic = markImage;
            toggle.SetIsOnWithoutNotify(value);
            toggle.onValueChanged.AddListener(Keep(DelegateSupport.ConvertDelegate<UnityAction<bool>>(new Action<bool>(v => { change.Invoke(v); dirty = true; }))!));
        }

        public void BeginSection(string title, bool expanded, Il2CppSystem.Action<bool> change)
        {
            folded = false;
            var r = Row("Section " + title, 30);
            var image = r.gameObject.AddComponent<Image>();
            image.color = new Color(0.13f, 0.14f, 0.15f);
            var button = r.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(Keep(Ui.Callback(() => { change.Invoke(!expanded); dirty = true; })));
            var t = Make("Label", r); Stretch(t, 10, 10);
            Text(t, (expanded ? "-   " : "+   ") + title, 16, new Color(0.95f, 0.72f, 0.35f)); // the game's font has no arrows
            folded = !expanded;
        }

        public void EndSections() => folded = false;
    }
}
