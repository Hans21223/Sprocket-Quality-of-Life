using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SprocketQoL;

/// Black box fix: on some graphics cards (older ones and old drivers most), smoke and fire leave a few bad pixels (not
/// a number, or infinite), and bloom and anti-aliasing smear them into black boxes. HDRP's Stop NaNs, off on the game's
/// cameras, blacks out just those pixels first. Checked once a second, as the game makes new cameras (battles, photos).
internal static class StopNaNs
{
    static readonly List<HDAdditionalCameraData> ours = new(); // turned on by us, turned back off with the setting
    static float next;
    static bool logged;

    internal static void Tick()
    {
        if (Time.unscaledTime < next) return;
        next = Time.unscaledTime + 1;
        if (Plugin.StopNaNs?.Value == false)
        {
            foreach (var hd in ours) if (hd != null) hd.stopNaNs = false;
            ours.Clear();
            return;
        }
        ours.RemoveAll(hd => hd == null);
        foreach (var cam in Camera.allCameras)
        {
            if (cam.GetComponent<HDAdditionalCameraData>() is not { } hd || hd.stopNaNs) continue;
            hd.stopNaNs = true;
            ours.Add(hd);
            if (!logged) { logged = true; Plugin.ModLog.LogInfo($"Black box fix: Stop NaNs turned on for the game's cameras ({cam.name} had it off)"); }
        }
    }
}
