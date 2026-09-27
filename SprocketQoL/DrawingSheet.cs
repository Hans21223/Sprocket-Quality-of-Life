using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.VehicleDesigner;
using Sprocket.Vehicles.Colliders;
using Sprocket.Vehicles.PlateStructures;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.HighDefinition;
using N = System.Numerics;

namespace SprocketQoL;

/// F9 in the vehicle editor: a drawing sheet of the vehicle, laid out as a maker's drawing: from above and from the
/// front on top, from the side and from the back below, all at one scale, with a 1 m ruler. Saved three times to
/// Documents\My Games\Sprocket\Photos: in lines only (black on white), in colour (the paint, lit evenly from every
/// side) with the same lines over it, and see-through (half the colour sheet, half the vehicle with its armour off). The lines come from the vehicle's own shapes (where faces meet at an angle, open
/// edges, the outline of curved parts), with what's behind other parts left out; each view's outline comes from its
/// picture, so parts whose shapes can't be read are still outlined.
internal static class DrawingSheet
{
    const int SheetWidth = 3600;   // pixels across the views
    const int Margin = 60;         // round the sheet
    const int Gap = 100;           // between the views
    const int DimLeft = 240;       // left of the views: the height and width dimensions and their measures
    const int DimBelow = 130;      // under a view: its length or width dimension and measure
    const int LabelHigh = 50;      // over a view: its name
    const int RulerHigh = 100;     // at the bottom: the 1 m ruler
    const int Pad = 6;             // round each view's picture
    const int Letter = 4;          // pixels per font pixel
    const int BlockGap = 60;       // between the ruler and the title block (the name, guns and description) under it
    const int BlockLine = 16;      // between the title block's parts
    const int FirstSettle = 40, Settle = 12; // frames each view is left to draw fully (exposure, then anti-aliasing)
    static readonly Color Behind = new(1, 0, 1); // drawn behind the vehicle to tell it apart: no paint is this magenta

    // Each view: the way the camera looks and the sheet's up. The vehicle's front is +z, its right +x.
    static readonly (string Name, Vector3 Look, Vector3 Up)[] Views =
    {
        ("top", Vector3.down, Vector3.right),   // from above, front to the left
        ("front", Vector3.back, Vector3.up),
        ("side", Vector3.right, Vector3.up),    // its left side, front to the left
        ("rear", Vector3.forward, Vector3.up),
    };

    static bool busy;

    internal static void Update()
    {
        if (busy || PhotoShot.Capturing || Keyboard.current is not { } keys || !keys.f9Key.wasPressedThisFrame || MeshTools.Typing()) return;
        if (DesignEditor.Instance is not { } editor) return;
        busy = true;
        editor.StartCoroutine(Run().WrapToIl2Cpp());
    }

    static System.Collections.IEnumerator Run()
    {
        Sheet? sheet = null;
        try
        {
            // Nothing under the mouse while the views are taken; a few frames for the editor to let go of what was.
            NoHover.On = true;
            for (int f = 0; f < 3; f++) yield return null;
            sheet = Sheet.Begin();
            if (sheet == null) yield break;
            // The views, then again with the armour off for the see-through sheet.
            foreach (bool inside in new[] { false, true })
            {
                if (inside) sheet.TakeArmourOff();
                for (int i = 0; i < Views.Length; i++)
                {
                    if (!sheet.Aim(i)) yield break;
                    for (int f = 0; f < (i == 0 ? FirstSettle : Settle); f++) { sheet.Hold(); yield return null; }
                    if (!sheet.Grab(i, inside)) yield break;
                }
            }
            sheet.PutArmourBack(); // its shapes make the lines
            DesignEditor.Instance?.Say("Drawing sheet: drawing the lines...", 10);
            yield return null; // the message shows before the work (a second or two)
            sheet.Finish();
        }
        finally
        {
            sheet?.End();
            NoHover.On = false;
            busy = false;
        }
    }

    /// The editor's hover highlight moves the part under the mouse onto a layer of its own (so it went missing from the
    /// pictures) and can turn the hull see-through; while the views are taken, the mouse is over nothing.
    [HarmonyPatch(typeof(VehicleComponentHoverer), nameof(VehicleComponentHoverer.SetHoverCandidate))]
    internal static class NoHover
    {
        internal static bool On;
        static void Prefix(ref IVehicleCollider item) { if (On) item = null!; }
    }

    /// One sheet in the making: the views' sizes on it, the camera that takes them, what it changed to take them.
    sealed class Sheet
    {
        Bounds box;
        float scale;                                   // pixels per metre, the same in every view
        readonly Drawing.View[] views = new Drawing.View[Views.Length];
        readonly byte[][] colour = new byte[Views.Length][]; // RGB, rows from the bottom
        readonly bool[][] solid = new bool[Views.Length][];  // the vehicle, not the backdrop
        readonly byte[][] insideColour = new byte[Views.Length][]; // the same with the armour off
        readonly bool[][] insideSolid = new bool[Views.Length][];
        readonly List<Renderer> armour = new();              // switched off for the see-through views
        Camera? cam;
        RenderTexture? target;
        int layers;
        int bottom = Margin;                          // where the drawing starts, over the title block
        bool fullbrightWas, fogWas;
        HashSet<IntPtr> aerials = new(); // the antennas' renderers
        // Everything else in the scene, out of sight while the views are taken: the editor's studio floor and wall
        // share the vehicle's layer, and would stand behind it.
        readonly List<Renderer> hidden = new();
        readonly List<Terrain> hiddenGround = new();
        int ownLayer = -1; // the layer the vehicle is moved onto for the pictures (-1: none free, its own layers used)
        readonly Dictionary<IntPtr, (GameObject Object, int Layer)> movedLayers = new();

        static N.Vector3 V(Vector3 v) => new(v.x, v.y, v.z);

        internal static Sheet? Begin()
        {
            var sheet = new Sheet();
            try { return sheet.Setup() ? sheet : null; }
            catch (Exception ex) { Fail("couldn't start", ex); sheet.End(); return null; }
        }

        bool Setup()
        {
            var main = Camera.main;
            // Only the vehicle, lit evenly: its own layers (no sky or the editor's handles), everything else hidden.
            var vehicle = new HashSet<IntPtr>();
            Bounds? around = null;
            // Antennas left off: a whip metres tall would set the vehicle's height and leave the views empty.
            aerials = MeshTools.AntennaRenderers();
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                foreach (var r in part.GetComponentsInChildren<Renderer>())
                {
                    if (aerials.Contains(r.Pointer) || !MeshTools.Drawn(r)) continue;
                    layers |= 1 << r.gameObject.layer;
                    vehicle.Add(r.Pointer);
                    if (around is { } a) { a.Encapsulate(r.bounds); around = a; } else around = r.bounds;
                }
            if (main == null || around is not { } b) { DesignEditor.Instance?.Say("Drawing sheet: no vehicle to draw", 4); return false; }
            box = b;
            var size = box.size;
            // Left column: the top and side views, as long as the vehicle. Right: the front and back, as wide.
            scale = (SheetWidth - Gap - 4 * Pad) / Math.Max(0.1f, size.z + size.x);
            for (int i = 0; i < Views.Length; i++)
            {
                var turn = Quaternion.LookRotation(Views[i].Look, Views[i].Up);
                var right = turn * Vector3.right;
                var up = turn * Vector3.up;
                float across = Math.Abs(Vector3.Dot(size, right)), high = Math.Abs(Vector3.Dot(size, up));
                int w = (int)MathF.Ceiling(across * scale) + 2 * Pad, h = (int)MathF.Ceiling(high * scale) + 2 * Pad;
                views[i] = new Drawing.View(V(box.center), V(right), V(up), V(Views[i].Look), scale, w, h);
            }
            var used = new HashSet<int>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
                if (o.TryCast<Renderer>() is { } r)
                {
                    used.Add(r.gameObject.layer);
                    if (r.enabled && !vehicle.Contains(r.Pointer)) { r.enabled = false; hidden.Add(r); }
                }
            foreach (var t in Terrain.activeTerrains)
                if (t != null && t.enabled) { t.enabled = false; hiddenGround.Add(t); }
            // The vehicle on a layer of its own while the views are taken, and the camera sees only that: what the map
            // draws without a renderer to switch off (its grass) stays out of the pictures. Back in End.
            for (int l = 31; l >= 8 && ownLayer < 0; l--)
                if (!used.Contains(l) && string.IsNullOrEmpty(LayerMask.LayerToName(l))) ownLayer = l;
            if (ownLayer >= 0)
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
                    if (o.TryCast<Renderer>() is { } r && vehicle.Contains(r.Pointer) && !movedLayers.ContainsKey(r.gameObject.Pointer))
                    {
                        movedLayers[r.gameObject.Pointer] = (r.gameObject, r.gameObject.layer);
                        r.gameObject.layer = ownLayer;
                    }
            // The game's fog, off too: it stood in front of the backdrop (below hull height, and all of the view from
            // above) and hazed the paint.
            fogWas = MeshTools.FogOff;
            MeshTools.Fog(off: true);
            fullbrightWas = MeshTools.FullbrightOn;
            if (!fullbrightWas) MeshTools.ToggleFullbright();
            var go = new GameObject("Quality of Life drawing camera");
            cam = go.AddComponent<Camera>();
            cam.CopyFrom(main);
            if (main.GetComponent<HDAdditionalCameraData>() is { } hd)
            {
                var own = go.AddComponent<HDAdditionalCameraData>();
                hd.CopyTo(own);
                own.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                own.backgroundColorHDR = Behind;
                // Not temporal: each view is still, but the camera jumps between them.
                own.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                // No reflections: seen from above, the paint mirrored the sky and the map's grass round the editor.
                own.customRenderingSettings = true;
                var overrides = own.renderingPathCustomFrameSettingsOverrideMask;
                foreach (var field in new[] { FrameSettingsField.SSR, FrameSettingsField.SSGI, FrameSettingsField.ReflectionProbe, FrameSettingsField.PlanarProbe, FrameSettingsField.SkyReflection })
                {
                    overrides.mask[(uint)field] = true;
                    own.renderingPathCustomFrameSettings.SetEnabled(field, false);
                }
                own.renderingPathCustomFrameSettingsOverrideMask = overrides;
            }
            cam.orthographic = true;
            cam.cullingMask = ownLayer >= 0 ? 1 << ownLayer : layers;
            DesignEditor.Instance?.Say("Drawing sheet: taking the views...", 10);
            Plugin.ModLog.LogInfo($"QOL_DRAWING vehicle {size.x:0.00} x {size.y:0.00} x {size.z:0.00} m, {scale:0} px a metre, views " +
                                  string.Join(", ", views.Select((v, i) => $"{Views[i].Name} {v.Width}x{v.Height}")) + $", {aerials.Count} antenna pieces left out, " +
                                  (ownLayer >= 0 ? $"{movedLayers.Count} objects on layer {ownLayer} for the pictures" : "no free layer: the vehicle's own layers"));
            return true;
        }

        /// Every frame the views are taken: the editor moves the part under the mouse to a highlight layer when the mouse
        /// goes over it or off it (out of the pictures), so any part it moved goes back on ours. The layer the editor last
        /// chose is the one the part gets back at the end.
        internal void Hold()
        {
            if (ownLayer < 0) return;
            List<IntPtr>? moved = null;
            foreach (var (key, (obj, _)) in movedLayers)
                if (obj != null && obj.layer != ownLayer) (moved ??= new()).Add(key);
            if (moved == null) return;
            foreach (var key in moved)
            {
                var obj = movedLayers[key].Object;
                movedLayers[key] = (obj, obj.layer);
                obj.layer = ownLayer;
            }
        }

        /// The camera straight at view `i`, just far enough back, drawing only the depth the vehicle fills.
        internal bool Aim(int i)
        {
            try
            {
                var v = views[i];
                var look = Views[i].Look;
                float radius = box.extents.magnitude;
                var at = box.center - look * (radius + 10);
                cam!.transform.SetPositionAndRotation(at, Quaternion.LookRotation(look, Views[i].Up));
                cam.orthographicSize = v.Height / 2f / scale;
                cam.aspect = v.Width / (float)v.Height;
                cam.nearClipPlane = Math.Max(0.01f, 10 - 0.1f);
                cam.farClipPlane = 10 + 2 * radius + 0.1f;
                if (target != null) { cam.targetTexture = null; target.Release(); UnityEngine.Object.Destroy(target); }
                target = new RenderTexture(v.Width, v.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                cam.targetTexture = target;
                return true;
            }
            catch (Exception ex) { Fail($"couldn't aim at the {Views[i].Name}", ex); return false; }
        }

        /// View `i` as the camera drew it: its colours, and which pixels are the vehicle (not the magenta behind it).
        /// The armour out of the pictures: every plate structure (hull, turret, add-on) and nothing hung on it.
        internal void TakeArmourOff()
        {
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                foreach (var r in part.GetComponentsInChildren<Renderer>())
                    if (r.enabled && r.GetComponentInParent<Sprocket.Vehicles.VehicleObject>()?.GetComponent<PlateStructure>() != null) { r.enabled = false; armour.Add(r); }
        }

        internal void PutArmourBack()
        {
            foreach (var r in armour) if (r != null) r.enabled = true;
            armour.Clear();
        }

        internal bool Grab(int i, bool inside)
        {
            try
            {
                var v = views[i];
                var was = RenderTexture.active;
                RenderTexture.active = target;
                var picture = new Texture2D(v.Width, v.Height, TextureFormat.RGBA32, 1, false);
                picture.ReadPixelsImpl(new Rect(0, 0, v.Width, v.Height), 0, 0, false);
                RenderTexture.active = was;
                var pixels = picture.GetPixels32();
                UnityEngine.Object.Destroy(picture);
                var rgb = new byte[v.Width * v.Height * 3];
                var mask = new bool[v.Width * v.Height];
                for (int p = 0; p < mask.Length; p++)
                {
                    var c = pixels[p];
                    rgb[p * 3] = c.r; rgb[p * 3 + 1] = c.g; rgb[p * 3 + 2] = c.b;
                    // The magenta behind (darker at the corners, with the game's vignette): red and blue alike, green far below.
                    int lo = Math.Min(c.r, c.b), hi = Math.Max(c.r, c.b);
                    mask[p] = !(lo > 30 && c.g < lo / 2 && hi - lo < hi * 0.35f);
                }
                (inside ? insideColour : colour)[i] = rgb;
                (inside ? insideSolid : solid)[i] = mask;
                var corner = pixels[0];
                Plugin.ModLog.LogInfo($"QOL_DRAWING {Views[i].Name}{(inside ? $" (armour off, {armour.Count} pieces)" : "")}: backdrop drawn as ({corner.r}, {corner.g}, {corner.b}), {mask.Count(m => m) * 100 / mask.Length}% of the picture is the vehicle");
                return true;
            }
            catch (Exception ex) { Fail($"couldn't take the {Views[i].Name} view", ex); return false; }
        }

        /// The lines of every view, both sheets put together and saved.
        internal void Finish()
        {
            try
            {
                var shapes = Shapes(out int unreadable);
                var ink = new bool[Views.Length][];
                for (int i = 0; i < Views.Length; i++)
                {
                    var v = views[i];
                    ink[i] = new bool[v.Width * v.Height];
                    var depth = Drawing.Depths(shapes, v);
                    // Every shape read: the vehicle is only where they are (unread ones are only in the picture).
                    if (unreadable == 0) { Drawing.Clip(solid[i], depth.Z, v.Width, v.Height, 3); Drawing.Clip(insideSolid[i], depth.Z, v.Width, v.Height, 3); }
                    Drawing.Lines(shapes, v, depth, ink[i]);
                    Drawing.Outline(solid[i], v.Width, v.Height, ink[i]);
                }
                // Top row: from above, then the front; below: the side, then the back (their bottoms level: the ground).
                // Left of the views and under them, room for the dimensions; over each, its name; under them the ruler,
                // and at the bottom the vehicle's name, guns and description.
                int left = Math.Max(views[0].Width, views[2].Width), right = Math.Max(views[1].Width, views[3].Width);
                int low = Math.Max(views[2].Height, views[3].Height), high = Math.Max(views[0].Height, views[1].Height);
                int xLeft = Margin + DimLeft, xRight = xLeft + left + Gap;
                var block = TitleBlock(xLeft, xRight + right - xLeft);
                int blockHigh = block.Height;
                bottom = Margin + (blockHigh > 0 ? blockHigh + BlockGap : 0);
                int yLow = bottom + RulerHigh + DimBelow, yHigh = yLow + low + LabelHigh + Gap + DimBelow;
                int w = xRight + right + Margin, h = yHigh + high + LabelHigh + Margin;
                var at = new (int X, int Y)[]
                {
                    (xLeft + (left - views[0].Width) / 2, yHigh + (high - views[0].Height) / 2),
                    (xRight + (right - views[1].Width) / 2, yHigh + (high - views[1].Height) / 2),
                    (xLeft + (left - views[2].Width) / 2, yLow),
                    (xRight + (right - views[3].Width) / 2, yLow),
                };
                var lines = new byte[w * h * 3];
                var painted = new byte[w * h * 3];
                var seeThrough = new byte[w * h * 3];
                Array.Fill(lines, (byte)255);
                Array.Fill(painted, (byte)255);
                Array.Fill(seeThrough, (byte)255);
                for (int i = 0; i < Views.Length; i++)
                {
                    var v = views[i];
                    for (int y = 0; y < v.Height; y++)
                        for (int x = 0; x < v.Width; x++)
                        {
                            int p = y * v.Width + x, q = ((at[i].Y + y) * w + at[i].X + x) * 3;
                            if (ink[i][p]) { Set(lines, q, 0); Set(painted, q, 30); Set(seeThrough, q, 30); continue; }
                            bool shell = solid[i][p], core = insideSolid[i][p];
                            for (int c = 0; c < 3; c++)
                            {
                                int paint = shell ? colour[i][p * 3 + c] : 255, under = core ? insideColour[i][p * 3 + c] : 255;
                                painted[q + c] = (byte)paint;
                                // Half the paint, half what's inside (white where there's nothing): the armour as glass.
                                seeThrough[q + c] = (byte)((paint + under) / 2);
                            }
                        }
                }
                foreach (var sheet in new[] { lines, painted, seeThrough }) Annotate(sheet, w, h, at, xLeft);
                // The title block, from its top down, over a rule the width of the views.
                if (blockHigh > 0)
                    foreach (var sheet in new[] { lines, painted, seeThrough })
                    {
                        Drawing.Box(sheet, w, h, xLeft, Margin + blockHigh + BlockGap / 2, xRight + right, Margin + blockHigh + BlockGap / 2 + 1, 0);
                        int top = Margin + blockHigh - 1;
                        foreach (var (x, yOffset, words) in block.Items)
                            Drawing.Stamp(sheet, w, h, x, top - yOffset, words, 0);
                    }
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Photos");
                Directory.CreateDirectory(dir);
                var name = Path.Combine(dir, $"Sprocket drawing {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
                Drawing.SavePng(name + ".png", w, h, lines);
                Drawing.SavePng(name + " (colour).png", w, h, painted);
                Drawing.SavePng(name + " (see-through).png", w, h, seeThrough);
                Plugin.ModLog.LogInfo($"QOL_DRAWING saved {name}.png, (colour) and (see-through), {w}x{h}: {shapes.Count} shapes drawn, {unreadable} meshes the game keeps unreadable (outlined only)");
                DesignEditor.Instance?.Say($"Drawing sheet saved (lines, colour and see-through): {name}.png", 8);
            }
            catch (Exception ex) { Fail("couldn't draw the sheet", ex); }
        }

        static void Set(byte[] rgb, int q, byte grey) { rgb[q] = grey; rgb[q + 1] = grey; rgb[q + 2] = grey; }

        sealed class TitleBlockLayout
        {
            public int Height;
            public readonly List<(int X, int YOffset, (int W, int H, byte[] Ink) Words)> Items = new();
        }

        /// The title block in three columns across `totalWidth` pixels:
        /// Column 1 (left): vehicle name (56pt bold), followed by horsepower and top speed (36pt).
        /// Column 2 (middle): weapons/armament (each kind of gun with caliber, name, L/length, count).
        /// Column 3 (right): vehicle description.
        /// Whatever can't be read is left out.
        static TitleBlockLayout TitleBlock(int xLeft, int totalWidth)
        {
            var layout = new TitleBlockLayout();
            try
            {
                string name = "", description = "";
                if (DesignEditor.Instance is { } editor && System.Text.Json.Nodes.JsonNode.Parse(editor.Snapshot())?["header"] is { } header)
                {
                    name = header["name"]?.GetValue<string>() ?? "";
                    description = header["desc"]?.GetValue<string>() ?? "";
                }
                var guns = new List<string>();
                var seen = new HashSet<IntPtr>();
                foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                    foreach (var cannon in part.GetComponentsInChildren<Sprocket.Vehicles.Cannons.Cannon>())
                        if (seen.Add(cannon.Pointer) && cannon.Blueprint is { Caliber: > 0 } gun)
                        {
                            string called = string.IsNullOrWhiteSpace(gun.Name) || gun.Name == "Unnamed Cannon" ? "" : "  " + gun.Name.Trim();
                            guns.Add($"{gun.Caliber} mm{called}   L/{gun.BarrelLength / (float)gun.Caliber:0.#}");
                        }
                var armament = string.Join("\n", guns.GroupBy(g => g).Select(g => (g.Count() > 1 ? $"{g.Count()} × " : "") + g.Key));

                var allComponents = DesignEditor.Instance?.AllComponents().ToList() ?? new();
                var engines = allComponents.Select(c => c.TryCast<Sprocket.Vehicles.Engines.CombustionEngine>()).Where(e => e?.Blueprint != null).OfType<Sprocket.Vehicles.Engines.CombustionEngine>().ToList();
                if (engines.Count == 0 && DesignEditor.Instance != null)
                    engines = DesignEditor.Instance.AllParts().SelectMany(p => p.GetComponentsInChildren<Sprocket.Vehicles.Engines.CombustionEngine>()).Where(e => e?.Blueprint != null).ToList();
                var engine = (engines.FirstOrDefault(e => e.SelectedInPowertrain) ?? engines.FirstOrDefault())?.Blueprint;

                var gearboxes = allComponents.Select(c => c.TryCast<Sprocket.Vehicles.Transmissions.TransmissionBlock>()).OfType<Sprocket.Vehicles.Transmissions.TransmissionBlock>().ToList();
                if (gearboxes.Count == 0 && DesignEditor.Instance != null)
                    gearboxes = DesignEditor.Instance.AllParts().SelectMany(p => p.GetComponentsInChildren<Sprocket.Vehicles.Transmissions.TransmissionBlock>()).ToList();
                var gearbox = gearboxes.FirstOrDefault(t => t.SelectedInPowertrain) ?? gearboxes.FirstOrDefault();

                var tracks = allComponents.Select(c => c.TryCast<Sprocket.Vehicles.Tracks.TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).OfType<Sprocket.Vehicles.Tracks.TrackAssembly>().ToList();
                if (tracks.Count == 0 && DesignEditor.Instance != null)
                    tracks = DesignEditor.Instance.AllParts().SelectMany(p => p.GetComponentsInChildren<Sprocket.Vehicles.Tracks.TrackAssembly>()).Where(t => t?.BlueprintSlot?.HasBlueprint == true).ToList();
                var track = tracks.FirstOrDefault();

                string powerText = "";
                if (engine is { MaxRPM: > 0, MaxTorque: > 0 })
                {
                    float hp = 0;
                    try
                    {
                        float idle = Math.Clamp(engine.IdleRPM, 1, Math.Max(1, engine.MaxRPM - 1)), max = Math.Max(1, engine.MaxRPM);
                        float peakKw = Enumerable.Range(0, 101)
                            .Select(k => idle + (max - idle) * k / 100f)
                            .Select(rpm => Sprocket.Engines.EngineRules.CalculatePowerAtRPM(engine.MaxTorque, rpm, max))
                            .DefaultIfEmpty(0)
                            .Max();
                        if (peakKw > 0) hp = peakKw * 1.341022f;
                    }
                    catch { }
                    if (hp <= 0) hp = engine.MaxTorque * engine.MaxRPM / 7120.54f;
                    string called = string.IsNullOrWhiteSpace(engine.Name) || engine.Name == "Unnamed Engine" || engine.Name == "Unnamed Combustion Engine" ? "" : "  " + engine.Name.Trim();
                    powerText = $"{MathF.Round(hp).ToString("0", System.Globalization.CultureInfo.InvariantCulture)} hp{called}";
                }

                string speedText = "";
                if (engine is { MaxRPM: > 0 } && gearbox != null && track?.BlueprintSlot?.HasBlueprint == true)
                {
                    var ratios = (gearbox.resultingDriveGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
                    var reverse = (gearbox.resultingReverseGearRatios?.ToArray() ?? Array.Empty<float>()).Select(Math.Abs).Where(r => r > 0).ToArray();
                    float finalDrive = track.BlueprintSlot.Blueprint.FinalDriveRatio;
                    var sprocket = track.SprocketAssembly?.WheelBlueprint;
                    float radius = sprocket?.HasBlueprint == true ? sprocket.Blueprint.Radius : 0;
                    if (ratios.Length > 0 && radius > 0 && finalDrive > 0)
                    {
                        float limit = float.MaxValue;
                        try { if (track.TopSpeed > 0) limit = track.TopSpeed * 3.6f; } catch { }
                        float Speed(float ratio) => Sprocket.VehicleDesigner.Powertrains.PowertrainInfo.CalculateSpeed(engine.MaxRPM, ratio * finalDrive, radius) * 3.6f;
                        float fwdSpeed = Math.Min(Speed(ratios.Min()), limit);
                        string fwdStr = MathF.Round(fwdSpeed).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                        if (reverse.Length > 0)
                        {
                            float revSpeed = Math.Min(Speed(reverse.Min()), limit);
                            string revStr = MathF.Round(revSpeed).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                            speedText = $"Top speed:  {fwdStr} km/h (forward) / {revStr} km/h (reverse)";
                        }
                        else
                        {
                            speedText = $"Top speed:  {fwdStr} km/h";
                        }
                    }
                }

                var mobility = string.Join("\n", new[] { powerText, speedText }.Where(s => !string.IsNullOrEmpty(s)));

                const int colGap = 80;
                const int headerGap = 10;
                const int sectionGap = 20;
                int colWidth = Math.Max(100, (totalWidth - 2 * colGap) / 3);
                int col1X = xLeft;
                int col2X = xLeft + colWidth + colGap;
                int col3X = xLeft + 2 * (colWidth + colGap);
                int col3Width = Math.Max(100, (xLeft + totalWidth) - col3X);

                var col1Items = new List<(int YOffset, (int W, int H, byte[] Ink) Words)>();
                int col1High = 0;
                if (Drawing.Words(name, 56, true, colWidth) is { } nameWords)
                {
                    col1Items.Add((col1High, nameWords));
                    col1High += nameWords.H;
                }
                if (!string.IsNullOrWhiteSpace(mobility))
                {
                    if (col1High > 0) col1High += sectionGap;
                    if (Drawing.Words("MOBILITY", 36, true, colWidth) is { } mobHeaderWords)
                    {
                        col1Items.Add((col1High, mobHeaderWords));
                        col1High += mobHeaderWords.H + headerGap;
                    }
                    if (Drawing.Words(mobility, 36, false, colWidth) is { } mobWords)
                    {
                        col1Items.Add((col1High, mobWords));
                        col1High += mobWords.H;
                    }
                }

                var col2Items = new List<(int YOffset, (int W, int H, byte[] Ink) Words)>();
                int col2High = 0;
                if (!string.IsNullOrWhiteSpace(armament))
                {
                    if (Drawing.Words("WEAPONS", 36, true, colWidth) is { } gunHeaderWords)
                    {
                        col2Items.Add((col2High, gunHeaderWords));
                        col2High += gunHeaderWords.H + headerGap;
                    }
                    if (Drawing.Words(armament, 36, false, colWidth) is { } gunWords)
                    {
                        col2Items.Add((col2High, gunWords));
                        col2High += gunWords.H;
                    }
                }

                var col3Items = new List<(int YOffset, (int W, int H, byte[] Ink) Words)>();
                int col3High = 0;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    if (Drawing.Words("DESCRIPTION", 36, true, col3Width) is { } descHeaderWords)
                    {
                        col3Items.Add((col3High, descHeaderWords));
                        col3High += descHeaderWords.H + headerGap;
                    }
                    if (Drawing.Words(description, 36, false, col3Width) is { } descWords)
                    {
                        col3Items.Add((col3High, descWords));
                        col3High += descWords.H;
                    }
                }

                int blockHigh = Math.Max(col1High, Math.Max(col2High, col3High));
                layout.Height = blockHigh;
                foreach (var (y, w) in col1Items) layout.Items.Add((col1X, y, w));
                foreach (var (y, w) in col2Items) layout.Items.Add((col2X, y, w));
                foreach (var (y, w) in col3Items) layout.Items.Add((col3X, y, w));

                Plugin.ModLog.LogInfo($"QOL_DRAWING title block: \"{name}\", {guns.Count} guns, {powerText}, {speedText}, {description.Length} characters of description");
            }
            catch (Exception ex) { Plugin.ModLog.LogWarning($"QOL_DRAWING no title block: {ex.Message}"); }
            return layout;
        }

        /// The sheet's writing: each view's name over it; the overall length and height beside the side view, the width
        /// beside the view from above and under the front view (to the centimetre); a 1 m ruler, ticks every 10 cm.
        void Annotate(byte[] rgb, int w, int h, (int X, int Y)[] at, int xLeft)
        {
            for (int i = 0; i < Views.Length; i++)
            {
                string name = Views[i].Name.ToUpperInvariant();
                Drawing.Text(rgb, w, h, name, at[i].X + (views[i].Width - Drawing.TextWidth(name, Letter)) / 2, at[i].Y + views[i].Height + 14, Letter, 0);
            }
            var size = box.size;
            int Px(float metres) => (int)MathF.Round(metres * scale);
            string M(float metres) => metres.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m"; // the pixel font has no comma
            (int x, int y) side = (at[2].X + Pad, at[2].Y + Pad), top = (at[0].X + Pad, at[0].Y + Pad), front = (at[1].X + Pad, at[1].Y + Pad);
            Drawing.Dimension(rgb, w, h, side.x, side.y - 50, side.x + Px(size.z), side.y - 50, M(size.z), Letter, 0);
            Drawing.Dimension(rgb, w, h, side.x - 50, side.y, side.x - 50, side.y + Px(size.y), M(size.y), Letter, 0);
            Drawing.Dimension(rgb, w, h, top.x - 50, top.y, top.x - 50, top.y + Px(size.x), M(size.x), Letter, 0);
            Drawing.Dimension(rgb, w, h, front.x, front.y - 50, front.x + Px(size.x), front.y - 50, M(size.x), Letter, 0);
            // The ruler.
            int x0 = xLeft, y0 = bottom + 50;
            Drawing.Box(rgb, w, h, x0, y0, x0 + Px(1), y0 + 2, 0);
            for (int k = 0; k <= 10; k++)
            {
                int x = x0 + (int)MathF.Round(k * scale / 10);
                Drawing.Box(rgb, w, h, x, y0, x + 1, y0 + (k % 5 == 0 ? 24 : 12), 0);
            }
            Drawing.Text(rgb, w, h, "1 m", x0 + Px(1) + 20, y0, Letter, 0);
        }

        /// Every mesh of the vehicle as a shape (world space). Some meshes the game keeps only on the graphics card:
        /// those are counted, and drawn by their outline alone.
        List<Drawing.Shape> Shapes(out int unreadable)
        {
            unreadable = 0;
            var shapes = new List<Drawing.Shape>();
            var seen = new HashSet<IntPtr>();
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                foreach (var filter in part.GetComponentsInChildren<MeshFilter>())
                {
                    if (!seen.Add(filter.Pointer)) continue; // a part's children are parts too
                    var r = filter.GetComponent<Renderer>();
                    var mesh = filter.sharedMesh;
                    if (r == null || aerials.Contains(r.Pointer) || !MeshTools.Drawn(r) || !r.gameObject.activeInHierarchy || mesh == null) continue;
                    if (!mesh.isReadable) { unreadable++; continue; }
                    shapes.Add(Shape(mesh, filter.transform.localToWorldMatrix));
                }
            // Crew figures (anything animated), as they stand now: they hide what's behind them and hold their place
            // in the picture. Their outline is the picture's; the lines inside a figure would only clutter it.
            foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
                foreach (var skin in part.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (!seen.Add(skin.Pointer) || aerials.Contains(skin.Pointer) || !MeshTools.Drawn(skin) || !skin.gameObject.activeInHierarchy) continue;
                    var posed = new Mesh();
                    try
                    {
                        skin.BakeMesh(posed, true); // with its scale; its place and turn below
                        var shape = Shape(posed, Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one));
                        shape.Edges = false;
                        shapes.Add(shape);
                    }
                    catch (Exception ex) { unreadable++; Plugin.ModLog.LogWarning($"QOL_DRAWING couldn't shape {skin.name}: {ex.Message}"); }
                    finally { UnityEngine.Object.Destroy(posed); }
                }
            return shapes;
        }

        /// A mesh (its own space) placed by `world`, as a shape.
        static Drawing.Shape Shape(Mesh mesh, Matrix4x4 world)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            var points = new N.Vector3[vertices.Length];
            for (int i = 0; i < points.Length; i++) points[i] = V(world.MultiplyPoint3x4(vertices[i]));
            var tris = new int[triangles.Length];
            for (int i = 0; i < tris.Length; i++) tris[i] = triangles[i];
            return Drawing.Weld(points, tris);
        }

        /// Everything as it was: the camera gone, the light and the floor back.
        internal void End()
        {
            try
            {
                if (cam != null) { cam.targetTexture = null; UnityEngine.Object.Destroy(cam.gameObject); }
                if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
                cam = null; target = null;
                if (!fullbrightWas && MeshTools.FullbrightOn) MeshTools.ToggleFullbright();
                foreach (var r in hidden) if (r != null) r.enabled = true;
                PutArmourBack();
                foreach (var t in hiddenGround) if (t != null) t.enabled = true;
                foreach (var (obj, layer) in movedLayers.Values) if (obj != null) obj.layer = layer;
                movedLayers.Clear();
                if (!fogWas) MeshTools.Fog(off: false);
                hidden.Clear();
                hiddenGround.Clear();
            }
            catch (Exception ex) { Plugin.ModLog.LogError($"QOL_DRAWING couldn't tidy up: {ex}"); }
        }

        static void Fail(string what, Exception ex)
        {
            Plugin.ModLog.LogError($"QOL_DRAWING {what}: {ex}");
            DesignEditor.Instance?.Say($"Drawing sheet: {what}: {ex.Message}", 8);
        }
    }
}
