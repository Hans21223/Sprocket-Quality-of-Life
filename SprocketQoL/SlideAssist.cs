using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.Transformations;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

/// What QoL adds to the game's edge and vertex slide (experimental 0.2.57 and later: Shift+V, or G twice):
/// - the amount beside the mouse while sliding: percent of the way, and millimetres the point has moved;
/// - a typed amount: digits (minus, a point, Backspace) while sliding set it in percent; the game's actions on those keys
///   are off while a slide runs (QoL's own keys too); a click, or Enter once typed, confirms, right-click or Esc cancels;
/// - holding Apply prediction (N) lines the slid point up with the nearest other point or edge middle under the mouse;
/// - after an edge slide, a box to change its amount, Even, Flip and Clamp afterwards, with the game's own slide maths
///   from where the points were before it (one undo step each);
/// - Loop cut (one cut) goes straight into sliding the new loop (MeshTools.LoopCut).
/// The slide stays the game's own (it mirrors with Mirror on). Its classes exist only on experimental, so they're found
/// while the game runs and read by reflection: on stable nothing attaches and nothing changes.
internal static class SlideAssist
{
    static Type? edgeType, vertexType;
    static MethodInfo? edgeApply, vertexApply; // each slide's ApplyElem: where it puts one point at an amount
    internal static bool Available => edgeType != null;
    internal static bool Sliding => sliding != null;

    /// From Plugin.Load: the hooks, when this Sprocket has the slide.
    internal static void Attach(Harmony harmony)
    {
        // In the assembly that holds MeshGeometryEditOp: AccessTools.TypeByName would read every game assembly, and the
        // interop ones that don't load fill the log with hundreds of lines.
        var assembly = typeof(MeshGeometryEditOp).Assembly;
        edgeType = assembly.GetType("Sprocket.PlateMesh.Operations.EdgeSlideOp");
        vertexType = assembly.GetType("Sprocket.PlateMesh.Operations.VertexSlideOp");
        if (edgeType == null) return; // stable: no slide
        edgeApply = edgeType.GetMethod("ApplyElem");
        vertexApply = vertexType?.GetMethod("ApplyElem");
        Hook(harmony, typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Update), nameof(BeforeUpdate), nameof(AfterUpdate));
        Hook(harmony, edgeType, "ApplyEdgeSlide", nameof(BeforeEdge), nameof(AfterEdge));
        if (vertexType != null) Hook(harmony, vertexType, "ApplyVertexSlide", nameof(BeforeVertex), nameof(AfterVertex));
    }

    static void Hook(Harmony harmony, Type type, string method, string prefix, string postfix)
    {
        string name = $"{type.Name}.{method}";
        try
        {
            var original = AccessTools.DeclaredMethod(type, method) ?? throw new MissingMethodException(name + " isn't in the game");
            var processor = harmony.CreateProcessor(original);
            processor.AddPrefix(new HarmonyMethod(AccessTools.Method(typeof(SlideAssist), prefix)));
            processor.AddPostfix(new HarmonyMethod(AccessTools.Method(typeof(SlideAssist), postfix)));
            processor.Patch();
            Hooks.Report.Add(new("Slide assist", name + " (prefix, postfix)", null));
        }
        catch (Exception ex) { Hooks.Failed("Slide assist", name, ex); }
    }

    // ---------- reading the game's slide (its classes aren't on stable, so by name) ----------

    static readonly Dictionary<(Type, string), MemberInfo?> members = new();

    static object? Get(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        if (!members.TryGetValue((t, name), out var m))
            members[(t, name)] = m = (MemberInfo?)t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                     ?? t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return m switch { PropertyInfo p => p.GetValue(o), FieldInfo f => f.GetValue(o), _ => null };
    }

    static T Read<T>(object? o, string name, T otherwise) => Get(o, name) is T v ? v : otherwise;

    static int Count(object? list) => list?.GetType() is { } t && (t.GetProperty("Count") ?? t.GetProperty("Length"))?.GetValue(list) is int n ? n : 0;

    /// Item i of a game list or array, or null.
    static object? At(object? list, int i) => i >= 0 && i < Count(list) ? list!.GetType().GetProperty("Item")?.GetValue(list, new object[] { i }) : null;

    /// A game list or array's items, in order.
    static List<object> Items(object? list)
    {
        var items = new List<object>();
        var item = list?.GetType().GetProperty("Item");
        for (int i = 0, n = Count(list); i < n; i++) if (item?.GetValue(list, new object[] { i }) is { } x) items.Add(x);
        return items;
    }

    /// The slide's active point (its amount is measured on it), without reading the others.
    static object? ActiveVert(object slide) => At(Get(slide, "slideVerts"), Read(slide, "activeIndex", 0));

    /// A vertex slide point's chosen way to slide.
    static object? Candidate(object vert) => At(Get(vert, "candidates"), Read(vert, "currentCandidate", 0));

    // ---------- the slide under way ----------

    static MeshGeometryEditOp? sliding;
    static int seenFrame = -10;
    static string typed = "";
    static float shownPercent, shownMm;
    static Vector3? lineFrom, lineTo; // local: the slid point where it lines up, and what it lines up with
    static readonly List<InputAction> silenced = new();

    static void BeforeUpdate(MeshGeometryEditOp __instance) => Ui.Guard("Slide", () =>
    {
        int mode = (int)__instance.mode;
        if (mode is not (4 or 5)) return; // VertexSlide, EdgeSlide
        if (sliding?.Pointer != __instance.Pointer) { End(); sliding = __instance; Silence(); last = null; }
        seenFrame = Time.frameCount;
        // Enter confirms a typed amount the way the game's click does: Update hands this request to its transformation.
        if (SlideMath.Typed(typed) != null && Keyboard.current is { } keys && (keys[Key.Enter].wasPressedThisFrame || keys[Key.NumpadEnter].wasPressedThisFrame))
            __instance.RequestedState = MeshEditOperatorState.Executed;
    });

    static void AfterUpdate(MeshGeometryEditOp __instance, MeshEditOperatorState __result) => Ui.Guard("Slide", () =>
    {
        if ((int)__instance.mode == 5 && __result == MeshEditOperatorState.Executed && (Plugin.SlidePanel?.Value ?? true)) Remember(__instance);
    });

    /// The amount QoL holds the slide at this frame: typed, or lined up while Apply prediction is held; else none.
    static float? Wanted(Func<float?> lineUp)
    {
        if (SlideMath.Typed(typed) is float fraction) { lineFrom = lineTo = null; return fraction; }
        if (Keybinds.Held("applyPrediction")) return lineUp();
        lineFrom = lineTo = null;
        return null;
    }

    // The game's edge slide amount is -((pointer - start) . screenDir) / screenHalfLen, rounded to `snap` when snapping
    // and held to -1..1 with Clamp: the pointer is put where that comes out at the wanted amount, snapping off.
    static void BeforeEdge(object __instance, Transformation __0) => Ui.Guard("Slide", () =>
    {
        if (__0 == null || Wanted(() => LineUpEdge(__instance)) is not float fac) return;
        var dir = Read(__instance, "screenDirUnit", Vector2.zero);
        float half = Read(__instance, "screenHalfLen", 0f);
        if (half <= 1e-4f || dir == Vector2.zero) return;
        __0.pointerPos = __0.initialPointerPos - fac * half * dir;
        __0.snap = 0;
    });

    static void AfterEdge(object __instance) => Ui.Guard("Slide", () =>
    {
        shownPercent = Read(__instance, "lastPerc", 0f) * 100;
        shownMm = ActiveVert(__instance) is { } vert ? MovedMm(vert) : 0;
    });

    // The game's vertex slide amount is the pointer's way along the active edge on screen, (pointer - start) . d / |d|^2.
    static void BeforeVertex(object __instance, Transformation __0) => Ui.Guard("Slide", () =>
    {
        if (__0 == null || Wanted(() => LineUpVertex(__instance)) is not float t || ActiveVert(__instance) is not { } vert || Candidate(vert) is not { } candidate) return;
        var d = Read(candidate, "screenDelta", Vector2.zero);
        if (d.sqrMagnitude < 1e-6f) return;
        __0.pointerPos = __0.initialPointerPos + t * d;
        __0.snap = 0;
    });

    static void AfterVertex(object __instance) => Ui.Guard("Slide", () =>
    {
        if (ActiveVert(__instance) is not { } vert || Candidate(vert) is not { } candidate || Mesh() is not { } mesh) return;
        var orig = Read(vert, "orig", Vector3.zero);
        var way = Read(candidate, "dest", orig) - orig;
        int index = Read(vert, "elementIndex", -1);
        var at = index >= 0 && index < mesh.vertices.Count ? mesh.vertices[index].position : orig;
        shownPercent = way.sqrMagnitude > 1e-12f ? Vector3.Dot(at - orig, way) / way.sqrMagnitude * 100 : 0;
        shownMm = MovedMm(vert);
    });

    static EditMesh? Mesh() => Hotkeys.Current?.meshEditor?.Mesh?.EditMesh;

    /// How far a slid point has moved, in millimetres in the vehicle.
    static float MovedMm(object vert)
    {
        if (Mesh() is not { } mesh || Hotkeys.Current is not { } e) return 0;
        int index = Read(vert, "elementIndex", -1);
        if (index < 0 || index >= mesh.vertices.Count) return 0;
        var moved = mesh.vertices[index].position - Read(vert, "orig", mesh.vertices[index].position);
        return (MeshSelection.TryMeshFrame(e, out var frame) ? frame.MultiplyVector(moved).magnitude : moved.magnitude) * 1000;
    }

    // ---------- lining up with a point ----------

    /// The point or edge middle (not slid) nearest the mouse on screen, within 40 pixels, in the shape's own space.
    static Vector3? Target(HashSet<int> slid)
    {
        if (Mesh() is not { } mesh || Hotkeys.Current is not { } e || Camera.main is not { } camera || Mouse.current is not { } mouse
            || !MeshSelection.TryMeshFrame(e, out var frame)) return null;
        var at = mouse.position.ReadValue();
        Vector3? best = null;
        float bestPx = 40 * 40;
        void Try(Vector3 local)
        {
            var s = camera.WorldToScreenPoint(frame.MultiplyPoint3x4(local));
            if (s.z <= 0) return;
            float d = (new Vector2(s.x, s.y) - at).sqrMagnitude;
            if (d < bestPx) { bestPx = d; best = local; }
        }
        var slidPoints = new HashSet<IntPtr>();
        for (int i = 0; i < mesh.vertices.Count; i++)
            if (slid.Contains(i)) slidPoints.Add(mesh.vertices[i].Pointer);
            else Try(mesh.vertices[i].position);
        for (int i = 0; i < mesh.edges.Count; i++)
        {
            var edge = mesh.edges[i];
            if (slidPoints.Contains(edge.v0.Pointer) && slidPoints.Contains(edge.v1.Pointer)) continue;
            Try((edge.v0.position + edge.v1.position) / 2);
        }
        return best;
    }

    /// The amount (from lo to hi) that puts `place(amount)` nearest `target`.
    static float Nearest(Func<float, Vector3> place, Vector3 target, float lo, float hi) => SlideMath.Nearest(f => (place(f) - target).sqrMagnitude, lo, hi);

    static float? LineUpEdge(object slide)
    {
        var verts = Items(Get(slide, "slideVerts"));
        int active = Read(slide, "activeIndex", 0);
        if (active < 0 || active >= verts.Count) return null;
        var slid = verts.Select(v => Read(v, "elementIndex", -1)).ToHashSet();
        if (Target(slid) is not Vector3 target) { lineFrom = lineTo = null; return null; }
        bool even = Read(slide, "UseEven", false), flip = Read(slide, "Flipped", false), clamp = Read(slide, "Clamp", true);
        int side = Read(slide, "currSideUnclamp", 0);
        float length = Read(verts[active], "edgeLen", 0f);
        Vector3 Place(float f) => EdgePlace(slide, verts[active], f, side, length, even, flip, clamp);
        float fac = Nearest(Place, target, clamp ? -1 : -3, clamp ? 1 : 3);
        lineFrom = Place(fac);
        lineTo = target;
        return fac;
    }

    static float? LineUpVertex(object slide)
    {
        var verts = Items(Get(slide, "slideVerts"));
        int active = Read(slide, "activeIndex", 0);
        var apply = vertexApply;
        if (active < 0 || active >= verts.Count || apply == null) return null;
        var slid = verts.Select(v => Read(v, "elementIndex", -1)).ToHashSet();
        if (Target(slid) is not Vector3 target) { lineFrom = lineTo = null; return null; }
        bool even = Read(slide, "UseEven", false), flip = Read(slide, "Flipped", false), clamp = Read(slide, "Clamp", true);
        Vector3 Place(float t) => apply.Invoke(slide, BindingFlags.DoNotWrapExceptions, null, new object[] { verts[active], t, even, flip }, null) is Vector3 p ? p : Vector3.zero;
        float best = Nearest(Place, target, clamp ? 0 : -2, clamp ? 1 : 3);
        lineFrom = Place(best);
        lineTo = target;
        return best;
    }

    /// Where the game's edge slide puts point `vert` at amount `fac`: its ApplyElem, with the side and length ApplyEdgeSlide
    /// works out first from the slide's kept side and its active point's edge length (Blender's edge slide).
    static Vector3 EdgePlace(object slide, object vert, float fac, int keptSide, float activeLength, bool even, bool flip, bool clamp)
    {
        var (side, length) = SlideMath.EdgeArgs(fac, even, flip, clamp, keptSide, activeLength);
        return edgeApply!.Invoke(slide, BindingFlags.DoNotWrapExceptions, null, new object[] { vert, fac, length, side, clamp, even, flip }, null) is Vector3 p ? p : Vector3.zero;
    }

    // ---------- the game's keys on the typing keys, off while a slide runs ----------

    static readonly HashSet<string> typingKeys = Enumerable.Range(0, 10).SelectMany(d => new[] { $"<keyboard>/{d}", $"<keyboard>/numpad{d}" })
        .Concat(new[] { "<keyboard>/minus", "<keyboard>/numpadminus", "<keyboard>/period", "<keyboard>/numpadperiod", "<keyboard>/comma", "<keyboard>/backspace" })
        .ToHashSet();

    static void Silence()
    {
        foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<InputActionAsset>()))
        {
            if (o.TryCast<InputActionAsset>() is not { } asset || asset.FindActionMap("MeshEdit", false) == null) continue;
            foreach (var map in asset.actionMaps)
                foreach (var action in map.actions)
                {
                    if (!action.enabled) continue;
                    for (int i = 0; i < action.bindings.Count; i++)
                        if (typingKeys.Contains((action.bindings[i].effectivePath ?? "").ToLowerInvariant())) { action.Disable(); silenced.Add(action); break; }
                }
        }
    }

    static void End()
    {
        foreach (var action in silenced) try { action.Enable(); } catch (Exception ex) { Plugin.ModLog.LogWarning("Slide: " + ex.Message); }
        silenced.Clear();
        sliding = null;
        typed = "";
        lineFrom = lineTo = null;
    }

    static readonly Key[] digits = { Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
    static readonly Key[] numpad = { Key.Numpad0, Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5, Key.Numpad6, Key.Numpad7, Key.Numpad8, Key.Numpad9 };

    /// A sign alone keeps the mouse's amount until a digit follows (it doesn't parse).
    static void TypeKeys(Keyboard keys)
    {
        for (int d = 0; d < 10; d++)
            if (keys[digits[d]].wasPressedThisFrame || keys[numpad[d]].wasPressedThisFrame) typed += (char)('0' + d);
        if ((keys[Key.Period].wasPressedThisFrame || keys[Key.NumpadPeriod].wasPressedThisFrame || keys[Key.Comma].wasPressedThisFrame) && !typed.Contains('.'))
            typed += typed.TrimStart('-').Length == 0 ? "0." : ".";
        if (keys[Key.Minus].wasPressedThisFrame || keys[Key.NumpadMinus].wasPressedThisFrame) typed = typed.StartsWith('-') ? typed[1..] : "-" + typed;
        if (keys[Key.Backspace].wasPressedThisFrame && typed.Length > 0) typed = typed[..^1];
    }

    // ---------- after an edge slide: change it ----------

    sealed class Last
    {
        public object Slide = null!;
        public IntPtr Editor;
        public int Vertices;
        public float Percent;
        public bool Even, Flip, Clamp;
        public Dictionary<int, Vector3> Expected = new();
    }
    static Last? last;
    const float BoxWidth = 260, Row = 24, Head = 24, Pad = 6;
    static readonly string[] rows = { "Amount", "Even", "Flip", "Clamp" };

    static void Remember(MeshGeometryEditOp op)
    {
        if (Get(op, "edgeSlideOp") is not { } slide || Hotkeys.Current is not { } e || Mesh() is not { } mesh) return;
        var verts = Items(Get(slide, "slideVerts"));
        if (verts.Count == 0) return;
        last = new Last
        {
            Slide = slide, Editor = e.Pointer, Vertices = mesh.vertices.Count, Percent = Read(slide, "lastPerc", 0f) * 100,
            Even = Read(slide, "UseEven", false), Flip = Read(slide, "Flipped", false), Clamp = Read(slide, "Clamp", true),
        };
        Expect(mesh, verts);
    }

    /// The slid points as they are now, so a later change from elsewhere closes the box.
    static void Expect(EditMesh mesh, List<object> verts)
    {
        if (last == null) return;
        last.Expected.Clear();
        foreach (var v in verts)
            if (Read(v, "elementIndex", -1) is int i && i >= 0 && i < mesh.vertices.Count) last.Expected[i] = mesh.vertices[i].position;
    }

    /// The last edge slide again from where its points were before it, with the box's amount and switches. False when it
    /// didn't go through (the editor was busy, or a face would fold over).
    static bool Again()
    {
        if (last == null || Hotkeys.Current is not { } e) return false;
        var verts = Items(Get(last.Slide, "slideVerts"));
        int active = Read(last.Slide, "activeIndex", 0);
        if (active < 0 || active >= verts.Count) return false;
        float fac = last.Percent / 100;
        if (last.Clamp) fac = Math.Clamp(fac, -1, 1);
        int side = Read(last.Slide, "currSideUnclamp", 0);
        float length = Read(verts[active], "edgeLen", 0f);
        var moved = new Dictionary<int, Num>();
        foreach (var v in verts)
        {
            var p = EdgePlace(last.Slide, v, fac, side, length, last.Even, last.Flip, last.Clamp);
            moved[Read(v, "elementIndex", -1)] = new Num(p.x, p.y, p.z);
        }
        moved.Remove(-1);
        bool mirror = e.meshEditor.Symmetry, done = false;
        MeshTools.Run(e, "Adjust slide", mesh =>
        {
            var result = MeshTools.MovePoints(mesh, new MeshTools.View(mesh), moved, mirror, "slid");
            done = result.Item1;
            return result;
        });
        if (done && Mesh() is { } now) { last.Vertices = now.vertices.Count; Expect(now, verts); }
        return done;
    }

    static Rect Box()
    {
        float height = Head + rows.Length * Row + Pad * 2;
        return new Rect(12, Math.Max(0, Screen.height - height - 120), BoxWidth, height);
    }

    static Rect RowRect(Rect box, int i) => new(box.x + Pad, box.y + Head + Pad + i * Row, box.width - Pad * 2, Row);
    static Rect Field(Rect row) => new(row.x + row.width * 0.45f, row.y + 2, row.width * 0.55f, row.height - 4);

    /// From DesignEditor.Update: the slide's end, typing, a deferred slide after Loop cut, and the box's clicks.
    internal static void Tick()
    {
        if (!Available) return;
        if (sliding != null && Time.frameCount - seenFrame > 2) End(); // confirmed, cancelled or gone
        if (sliding != null && Keyboard.current is { } keys) TypeKeys(keys);
        if (slideNext is { } next && Time.frameCount > slideAfter) { slideNext = null; StartEdgeSlide(next); }
        if (last == null) return;
        if (Hotkeys.Current is not { } e || e.Pointer != last.Editor || Mesh() is not { } mesh || mesh.vertices.Count != last.Vertices
            || last.Expected.Any(kv => kv.Key >= mesh.vertices.Count || (mesh.vertices[kv.Key].position - kv.Value).sqrMagnitude > 1e-10f)
            || Keyboard.current?.escapeKey.wasPressedThisFrame == true || sliding != null)
        { last = null; return; } // the design changed some other way, or a new slide started
        if (Mouse.current is not { } mouse) return;
        var p = mouse.position.ReadValue();
        var gui = new Vector2(p.x, Screen.height - p.y);
        var box = Box();
        if (!box.Contains(gui)) return;
        InputShield.Claim(); // the game's mouse waits while over the box
        if (!mouse.leftButton.wasPressedThisFrame) return;
        if (gui.y < box.y + Head) { if (gui.x > box.xMax - 22) last = null; return; } // X
        bool shift = Keyboard.current?.shiftKey.isPressed == true;
        var before = (last.Percent, last.Even, last.Flip, last.Clamp);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = RowRect(box, i);
            if (!row.Contains(gui)) continue;
            var field = Field(row);
            switch (i)
            {
                case 0 when gui.x < field.x + 18: last.Percent -= shift ? 10 : 1; break;
                case 0 when gui.x > field.xMax - 18: last.Percent += shift ? 10 : 1; break;
                case 0: return;
                case 1: last.Even = !last.Even; break;
                case 2: last.Flip = !last.Flip; break;
                case 3: last.Clamp = !last.Clamp; break;
            }
            if (last.Clamp) last.Percent = Math.Clamp(last.Percent, -100, 100);
            if (!Again()) (last.Percent, last.Even, last.Flip, last.Clamp) = before; // refused: the box keeps what the shape has
            return;
        }
    }

    static GUIStyle? style, label, value, title;

    /// From DesignEditor.OnGUI: the amount by the mouse while sliding, the line-up guide, and the box after an edge slide.
    internal static void Draw()
    {
        if (!Available) return;
        if (sliding != null && Time.frameCount - seenFrame <= 2 && Mouse.current is { } mouse)
        {
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            string text = $"{shownPercent:0.#} %  ·  {shownMm:0.#} mm"
                + (typed.Length > 0 ? $"\ntyped {typed} %  (click or Enter)" : lineTo != null ? "\nlined up" : "");
            var p = mouse.position.ReadValue();
            var rect = new Rect(p.x + 18, Screen.height - p.y + 14, 190, typed.Length > 0 || lineTo != null ? 44 : 26);
            GUI.Box(rect, ""); GUI.Box(rect, ""); // twice: the skin's box is see-through
            GUI.Label(rect, text, style);
            if (lineFrom is Vector3 a && lineTo is Vector3 b && Hotkeys.Current is { } e && Camera.main is { } camera && MeshSelection.TryMeshFrame(e, out var frame))
            {
                var sa = camera.WorldToScreenPoint(frame.MultiplyPoint3x4(a));
                var sb = camera.WorldToScreenPoint(frame.MultiplyPoint3x4(b));
                if (sa.z > 0 && sb.z > 0) GeometryPreview.DrawScreenLines(new[] { (Vector2)sa, (Vector2)sb }, new Color(0.3f, 1f, 0.5f), 2);
            }
        }
        if (last == null) return;
        label ??= new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
        value ??= new GUIStyle(label) { alignment = TextAnchor.MiddleCenter };
        title ??= new GUIStyle(label) { fontStyle = FontStyle.Bold };
        var box = Box();
        GUI.Box(box, ""); GUI.Box(box, "");
        GUI.Label(new Rect(box.x + 8, box.y, box.width - 32, Head), "Edge slide" + (DesignEditor.Instance?.IsBusy == true ? "  ..." : ""), title);
        GUI.Label(new Rect(box.xMax - 20, box.y, 16, Head), "x", title);
        for (int i = 0; i < rows.Length; i++)
        {
            var row = RowRect(box, i);
            if (i == 0)
            {
                var field = Field(row);
                GUI.Label(row, "Amount (%)", label);
                GUI.Box(field, "");
                GUI.Label(new Rect(field.x + 4, field.y, 14, field.height), "-", value);
                GUI.Label(field, last.Percent.ToString("0.#", CultureInfo.InvariantCulture), value);
                GUI.Label(new Rect(field.xMax - 18, field.y, 14, field.height), "+", value);
                continue;
            }
            bool on = i switch { 1 => last.Even, 2 => last.Flip, _ => last.Clamp };
            GUI.Box(new Rect(row.x, row.y + 4, 16, 16), on ? "x" : "");
            GUI.Label(new Rect(row.x + 24, row.y, row.width - 24, row.height), rows[i], label);
        }
    }

    // ---------- Loop cut, then slide ----------

    static PlateStructureEditor? slideNext;
    static int slideAfter;

    /// The shape's points now, to tell the new ones afterwards.
    internal static HashSet<IntPtr> Points(EditMesh mesh)
    {
        var set = new HashSet<IntPtr>();
        for (int i = 0; i < mesh.vertices.Count; i++) set.Add(mesh.vertices[i].Pointer);
        return set;
    }

    /// Selects only the loop a cut just made (edges with both ends new): one side's with Mirror on (the game's slide
    /// moves the mirror image itself). False when the cut made no loop.
    internal static bool SelectNewLoop(EditMesh mesh, HashSet<IntPtr> before, bool mirror)
    {
        var loop = new List<Edge>();
        for (int i = 0; i < mesh.edges.Count; i++)
        {
            var edge = mesh.edges[i];
            if (!before.Contains(edge.v0.Pointer) && !before.Contains(edge.v1.Pointer)) loop.Add(edge);
        }
        if (mirror && loop.Where(x => x.v0.position.x >= -MeshTools.Tolerance && x.v1.position.x >= -MeshTools.Tolerance).ToList() is { Count: > 0 } right)
            loop = right;
        if (loop.Count == 0) return false;
        var points = loop.SelectMany(x => new[] { x.v0, x.v1 }).GroupBy(v => v.Pointer).Select(g => g.First()).ToList();
        MirrorMerge.Selection.Only(mesh, points);
        foreach (var x in loop) { x.EnableFlag(ElementFlags.Selected); mesh.selection.Add(x); }
        mesh.selectedEdgeCount = (ushort)Math.Min(loop.Count, ushort.MaxValue);
        mesh.MarkDirty(MeshDirtyFlags.All);
        return true;
    }

    /// The game's edge slide on the selection, a frame from now (once the cut's edit has settled).
    internal static void SlideSoon(PlateStructureEditor editor) { slideNext = editor; slideAfter = Time.frameCount + 1; }

    static void StartEdgeSlide(PlateStructureEditor editor) => Ui.Guard("Slide after cut", () =>
    {
        if (editor == null || editor.operations == null) return;
        typeof(PlateStructureEditOperations).GetMethod("EdgeSlide", new[] { typeof(int) })
            ?.Invoke(editor.operations, BindingFlags.DoNotWrapExceptions, null, new object[] { -1 }, null);
    });
}
