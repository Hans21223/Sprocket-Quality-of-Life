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
    static bool overlayWas;
    static string file = "";

    /// While true nothing of the mod draws on screen (it would be in the photo).
    internal static bool Capturing => step >= 0;

    internal static void Update()
    {
        try
        {
            if (step >= 0) { Advance(); return; }
            if (Keyboard.current is not { } keys || !keys.f8Key.wasPressedThisFrame) return;
            overlay = UnityEngine.Object.FindObjectOfType<PhotomodeOverlay>();
            app = UnityEngine.Object.FindObjectOfType<SprocketApplication>();
            if (overlay == null || app == null) return; // not in photo mode
            var current = app.CurrentSettings;
            before = Profile(current, current.Graphics.Copy());
            var max = Profile(current, current.Graphics.Copy());
            Maximise(max.Graphics);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Sprocket", "Photos");
            Directory.CreateDirectory(dir);
            file = Path.Combine(dir, $"Sprocket {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            overlayWas = overlay.overlayVisible;
            overlay.SetOverlayVisible(false);
            app.ApplySettings(max);
            step = 0; frames = 0;
            Plugin.ModLog.LogInfo($"QOL_PHOTO max settings applied, taking the photo in {SettleFrames} frames");
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
            frames++;
            if (step == 0 && frames >= SettleFrames)
            {
                ScreenCapture.CaptureScreenshot(file); // written at the end of this frame
                step = 1; frames = 0;
            }
            else if (step == 1 && frames >= 3)
            {
                Restore();
                Plugin.ModLog.LogInfo($"QOL_PHOTO saved {file} ({(File.Exists(file) ? new FileInfo(file).Length / 1024 + " KB" : "not written yet")})");
                DesignEditor.Instance?.Say("Photo saved: " + file, 6);
            }
        }
        catch (Exception ex)
        {
            Plugin.ModLog.LogError($"QOL_PHOTO failed: {ex}");
            Restore();
        }
    }

    /// Settings back as they were, and the overlay if it was showing.
    static void Restore()
    {
        step = -1;
        try { if (app != null && before != null) app.ApplySettings(before); }
        catch (Exception ex) { Plugin.ModLog.LogError($"QOL_PHOTO couldn't put the graphics settings back (reopen Settings to fix): {ex}"); }
        try { if (overlay != null && overlayWas) overlay.SetOverlayVisible(true); } catch { }
        before = null;
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
        // Levels without a known top value: as in the game's best preset.
        var presets = Resources.FindObjectsOfTypeAll(Il2CppType.Of<GraphicsSettingsContainer>())
            .Select(o => o.TryCast<GraphicsSettingsContainer>()).Where(p => p?.Settings != null).ToList();
        if (presets.Count == 0) return;
        var best = presets.OrderBy(p => (int)p!.Settings.ShadowQuality + (int)p.Settings.TextureResolution + (int)p.Settings.AmbientOcclusionMode
                                        + (int)p.Settings.ScreenSpaceReflectionMode + (int)p.Settings.DistantObjectDetail).Last()!.Settings;
        g.QualityLevel = best.QualityLevel;
        g.ShaderQualityLevel = best.ShaderQualityLevel;
        g.TerrainQualityLevel = best.TerrainQualityLevel;
        Plugin.ModLog.LogInfo($"QOL_PHOTO presets {string.Join(", ", presets.Select(p => $"{p!.Name} (quality {p.Settings.QualityLevel}, shader {p.Settings.ShaderQualityLevel}, terrain {p.Settings.TerrainQualityLevel})"))}");
    }
}
