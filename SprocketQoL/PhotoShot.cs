using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using Sprocket;
using Sprocket.Photomode;
using Sprocket.SettingConfiguration;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SprocketQoL;

/// F8 in photo mode: a photo at the highest graphics settings without leaving photo mode. The settings go up and the
/// photo mode overlay hides for the shot, then both come back. Saved to Documents\My Games\Sprocket\Photos.
internal static class PhotoShot
{
    const int SettleFrames = 45; // shadows, textures and screen-space effects catch up with the new settings
    static int step = -1, frames;
    static PhotomodeOverlay? overlay;
    static SprocketApplication? app;
    static SettingsProfile? before;
    static bool overlayWas, overlayCaptured;
    static int captureId;
    static string file = "";
    static string resolution = "Screen", method = "Render";
    static RenderPhoto? activeFrame;

    /// While true nothing of the mod draws on screen (it would be in the photo).
    internal static bool Capturing => step >= 0;

    internal static void Update()
    {
        try
        {
            if (step >= 0) { Advance(); return; }
            if (DrawingSheet.Capturing || !Keybinds.Pressed("photo") || MeshTools.Typing()) return;
            overlay = UnityEngine.Object.FindObjectOfType<PhotomodeOverlay>();
            app = UnityEngine.Object.FindObjectOfType<SprocketApplication>();
            if (overlay == null || app == null) return; // not in photo mode
            resolution = Plugin.PhotoResolution?.Value ?? "Screen";
            method = Plugin.PhotoMethod?.Value ?? "Render";
            var size = PhotoOutput.SizeFor(Screen.width, Screen.height, resolution);
            if (resolution != "Screen" && method == "Render" && !PhotoOutput.FitsRenderTarget(size, SystemInfo.maxTextureSize))
                throw new InvalidOperationException("This GPU cannot render the chosen photo size. Choose a smaller resolution or Upscale in Mod Options > Photos.");
            overlayWas = overlay.overlayVisible;
            overlayCaptured = true;
            captureId++;
            var current = app.CurrentSettings;
            before = Profile(current, current.Graphics.Copy());
            var max = Profile(current, current.Graphics.Copy());
            Maximise(max.Graphics);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Photos");
            Directory.CreateDirectory(dir);
            file = Drawing.UnusedPath(Path.Combine(dir, $"Sprocket {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png"));
            overlay.SetOverlayVisible(false);
            app.ApplySettings(max);
            step = 0; frames = 0;
            Plugin.ModLog.LogInfo($"QOL_PHOTO {resolution}, {method}; max settings applied, taking the photo in {SettleFrames} frames");
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"QOL_PHOTO couldn't start: {ex}");
            Restore();
        }
    }

    static void Advance()
    {
        try
        {
            if (overlay == null || app == null || DesignEditor.Instance == null) { Restore(); return; }
            frames++;
            if (step == 0 && frames >= SettleFrames)
            {
                shotDone = false; shotError = null;
                if (DesignEditor.Instance is { } editor) editor.StartCoroutine(Shoot(captureId, file).WrapToIl2Cpp());
                else throw new InvalidOperationException("no editor to run photo coroutine");
                step = 1; frames = 0;
            }
            else if (step == 1 && (shotDone || frames > 120))
            {
                Restore();
                if (shotDone && shotError == null)
                {
                    long kb = File.Exists(file) ? new FileInfo(file).Length / 1024 : 0;
                    Plugin.ModLog.LogInfo($"QOL_PHOTO saved {file} ({kb} KB)");
                    DesignEditor.Instance?.Say("Photo saved: " + file, 6);
                }
                else
                {
                    Plugin.ModLog.LogError($"QOL_PHOTO not saved: {shotError?.ToString() ?? "the frame never came"}");
                    DesignEditor.Instance?.Say("Photo not saved: " + (shotError?.Message ?? "the frame never came"), 6);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"QOL_PHOTO failed: {ex}");
            Restore();
        }
    }

    static bool shotDone;
    static Exception? shotError;

    /// The finished frame, as shown on screen, saved as a PNG. (The game's own ScreenCapture.CaptureScreenshot can't be
    /// called from a mod: its file name doesn't pass through.)
    static System.Collections.IEnumerator Shoot(int id, string destination)
    {
        yield return new WaitForEndOfFrame(); // after everything is drawn
        // A timed-out or cancelled coroutine must not capture restored settings, overwrite a
        // later shot's destination, or signal completion for a new capture.
        if (id != captureId || step < 0) yield break;
        Texture2D? shot = null;
        RenderPhoto? frame = null;
        try
        {
            PhotoOutput.Size size = default;
            try
            {
                size = PhotoOutput.SizeFor(Screen.width, Screen.height, resolution);
                if (resolution != "Screen" && method == "Render")
                    activeFrame = frame = new RenderPhoto(Camera.main ?? throw new InvalidOperationException("No photo camera is active."), size);
            }
            catch (Exception ex) { if (id == captureId) shotError = ex; }
            if (frame != null) yield return new WaitForEndOfFrame(); // HDRP renders the offscreen camera in the next frame.
            if (id != captureId || step < 0) yield break;
            try
            {
                if (shotError != null) throw shotError;
                shot = frame != null ? frame.Read() : ScreenCapture.CaptureScreenshotAsTexture();
                if (shot == null) throw new InvalidOperationException("The photo frame was unavailable.");
                var pixels = shot.GetPixels32();
                var rgb = new byte[checked(pixels.Length * 3)];
                int nonOpaque = 0;
                for (int p = 0; p < pixels.Length; p++)
                {
                    var pixel = pixels[p];
                    rgb[p * 3] = pixel.r; rgb[p * 3 + 1] = pixel.g; rgb[p * 3 + 2] = pixel.b;
                    if (pixel.a != 255) nonOpaque++;
                }
                // HDRP's final RGB already contains smoke blended over the scene, but its alpha may still contain
                // particle coverage/distortion values. Encoding that alpha makes viewers blend the smoke AGAIN.
                // Save an opaque RGB photograph without multiplying or compositing its already-finished colours.
                // GetPixels32 and SavePng both use bottom-up rows, so the original orientation is preserved.
                var outputSize = resolution == "Screen" ? new PhotoOutput.Size(shot.width, shot.height) : size;
                rgb = PhotoOutput.ResizeRgb(rgb, shot.width, shot.height, outputSize);
                Drawing.SavePng(destination, outputSize.Width, outputSize.Height, rgb);
                Plugin.ModLog.LogInfo($"QOL_PHOTO {outputSize.Width}x{outputSize.Height}, {method}, opaque RGB output; discarded render alpha on {nonOpaque} pixels");
            }
            catch (Exception ex) { if (id == captureId) shotError = ex; }
        }
        finally
        {
            if (shot != null) UnityEngine.Object.Destroy(shot);
            frame?.Dispose();
            if (ReferenceEquals(activeFrame, frame)) activeFrame = null;
        }
        if (id == captureId) shotDone = true;
    }

    sealed class RenderPhoto : IDisposable
    {
        Camera? camera;
        RenderTexture? target;
        RenderTexture? previousTarget;
        Rect previousRect;
        float previousAspect;
        bool previousDynamicResolution;
        bool ownsCameraState;
        readonly PhotoOutput.Size size;
        internal RenderPhoto(Camera source, PhotoOutput.Size size)
        {
            this.size = size;
            try
            {
                target = new RenderTexture(size.Width, size.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                if (!target.Create()) throw new InvalidOperationException("Could not allocate the high-resolution photo. Choose a smaller size or Upscale.");
                // Use the actual photo camera, so native vehicle/track rendering and camera-specific effects
                // run normally. Only its output target changes for one frame; never resize the game window.
                camera = source;
                previousTarget = source.targetTexture; previousRect = source.rect; previousAspect = source.aspect;
                previousDynamicResolution = source.allowDynamicResolution; ownsCameraState = true;
                camera.targetTexture = target;
                camera.rect = new Rect(0, 0, 1, 1); camera.aspect = size.Width / (float)size.Height;
                camera.allowDynamicResolution = false;
            }
            catch { Dispose(); throw; }
        }
        internal Texture2D Read()
        {
            if (target == null) throw new InvalidOperationException("The photo render was cancelled.");
            if (camera == null || camera.targetTexture != target) throw new InvalidOperationException("The photo camera changed before its frame was ready.");
            Texture2D? image = null;
            var previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                image = new Texture2D(size.Width, size.Height, TextureFormat.RGBA32, 1, false);
                image.ReadPixelsImpl(new Rect(0, 0, size.Width, size.Height), 0, 0, false);
                return image;
            }
            catch { if (image != null) UnityEngine.Object.Destroy(image); throw; }
            finally { RenderTexture.active = previous; }
        }
        public void Dispose()
        {
            if (camera != null && ownsCameraState)
            {
                // If another tool took ownership meanwhile, leave its camera target/settings alone.
                if (camera.targetTexture == target)
                {
                    camera.targetTexture = previousTarget; camera.rect = previousRect; camera.aspect = previousAspect;
                    camera.allowDynamicResolution = previousDynamicResolution;
                }
            }
            ownsCameraState = false;
            camera = null;
            if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
            target = null;
        }
    }

    /// Settings back as they were, and the overlay if it was showing.
    static void Restore()
    {
        step = -1;
        captureId++;
        try { activeFrame?.Dispose(); }
        catch (Exception ex) { Plugin.ModLog.LogError($"QOL_PHOTO couldn't release the render target: {ex}"); }
        activeFrame = null;
        try { if (app != null && before != null) app.ApplySettings(before); }
        catch (Exception ex) { Plugin.ModLog.LogError($"QOL_PHOTO couldn't put the graphics settings back (reopen Settings to fix): {ex}"); }
        try { if (overlay != null && overlayCaptured) overlay.SetOverlayVisible(overlayWas); } catch { }
        before = null; app = null; overlay = null; overlayCaptured = false;
    }

    /// A copy of the player's settings with its own graphics settings (the copy's may be shared with the original).
    static SettingsProfile Profile(SettingsProfile source, GraphicsSettings graphics)
    {
        var copy = new SettingsProfile(source);
        copy.graphics = graphics;
        return copy;
    }

    /// Every quality setting at its best. Looks (vignette, film grain, blur, depth of field on or off), resolution and
    /// anti-aliasing type stay as the player set them.
    static void Maximise(GraphicsSettings g)
    {
        g.TextureResolution = GraphicsSettings.TextureResolutionMode.High;
        g.ShadowDistanceMode = GraphicsSettings.ShadowDistanceModeType.VeryFar;
        g.ShadowQuality = GraphicsSettings.ShadowQualityMode.Ultra;
        g.AmbientOcclusionMode = GraphicsSettings.AmbientOcclusionModeType.High;
        g.ContactShadowsMode = GraphicsSettings.ContactShadowModeType.High;
        g.ContactShadowsEnabled = true;
        g.ScreenSpaceGlobalIlluminationMode = GraphicsSettings.ScreenSpaceGlobalIlluminationModeType.High;
        g.ScreenSpaceReflectionMode = GraphicsSettings.ScreenSpaceReflectionModeType.High;
        g.ScreenSpaceReflections = true;
        g.MicroShadows = true;
        g.DistantObjectDetail = GraphicsSettings.DistantObjectDetailMode.High;
        g.GrassDistanceMode = GraphicsSettings.GrassDistanceModeType.Far;
        g.GroundDetailDensityMode = GraphicsSettings.GroundDetailDensityModeType.High;
        g.DepthOfField = GraphicsSettings.DepthOfFieldQualityMode.High;
        g.AnisotropicFiltering = AnisotropicFiltering.ForceEnable;
        g.AntiAliasingQuality = 2;
        g.DynamicResolutionMode = DynamicResolutionMode.Off; // full resolution, no upscaling
        g.DLSS = false;
        // Levels without a known top value: the game's "Maximum" preset's, unless the player's are better already
        // (the presets run quality 3 at Minimum to 0 at Maximum, terrain and shader the other way).
        var presets = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GraphicsSettingsContainer>())
            .Select(o => o.TryCast<GraphicsSettingsContainer>()).Where(p => p?.Settings != null).ToList();
        if (presets.FirstOrDefault(p => p!.Name == "Maximum")?.Settings is not { } best) return;
        g.QualityLevel = Math.Min(g.QualityLevel, best.QualityLevel);
        g.ShaderQualityLevel = Math.Max(g.ShaderQualityLevel, best.ShaderQualityLevel);
        g.TerrainQualityLevel = Math.Max(g.TerrainQualityLevel, best.TerrainQualityLevel);
        Plugin.ModLog.LogInfo($"QOL_PHOTO presets {string.Join(", ", presets.Select(p => $"{p!.Name} (quality {p.Settings.QualityLevel}, shader {p.Settings.ShaderQualityLevel}, terrain {p.Settings.TerrainQualityLevel})"))}");
    }
}
