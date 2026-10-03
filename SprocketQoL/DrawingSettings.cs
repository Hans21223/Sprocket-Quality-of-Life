using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.Turrets.Editor;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Cannons;

namespace SprocketQoL;

[HarmonyPatch]
public static class DrawingSettings
{
    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Structure(PlateStructureEditor __instance, IGUILayout layout) => Draw(layout, () => __instance.RequestRedraw());
    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(TurretRingEditor __instance, IGUILayout layout) => Draw(layout, () => __instance.RequestRedraw());
    [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    static void Cannon(CannonEditor __instance, IGUILayout layout) => Draw(layout, () => __instance.RequestRedraw(), __instance.Component);

    internal static string GunKey(Cannon gun) => GunAnnotationPreferences.Key(gun.Vehicle?.DesignInfo?.Name, (int)gun.VehicleObject.VUID);

    static string? cachedLimits;
    static GunAnnotationPreferences gunLimits = new(null);

    static void Draw(IGUILayout layout, Action redraw, Cannon? gun = null) => Ui.Inspector("Drawing sheet settings", layout, () =>
    {
        var ui = Ui.Drawer(layout);
        if (ui == null) return;
        void Toggle(string label, bool value, Action<bool> set, string help) =>
            ui.ToggleField(label, value, Ui.BoolCallback(v => { set(v); redraw(); }), help);

        Ui.Section(layout, "Drawing sheet");
        ui.InfoField($"Press {Keybinds.Shown("drawing")} to save drawing sheets to Photos. These settings are remembered.", 2);

        bool lines = !(Plugin.DrawingNoWireframe?.Value ?? false);
        Toggle("Show drawing lines", lines,
            v => { if (Plugin.DrawingNoWireframe != null) Plugin.DrawingNoWireframe.Value = !v; },
            "Adds outlines or wireframe to colour and see-through drawings. Off: exports those views without added lines. The separate lines-only sheet and blue blueprint keep their lines.");
        ui.Slider("Line strength (%)", Plugin.DrawingIntensity?.Value ?? 100, 0, 100,
            Ui.FloatCallback(v => { if (Plugin.DrawingIntensity != null) Plugin.DrawingIntensity.Value = MathF.Round(v); }));
        if (!lines) ui.InfoField("Line strength still applies to lines-only and blue blueprint sheets.", 1);
        if (lines)
        {
            Toggle("Colour: clean outlines", Plugin.DrawingColourOutline?.Value ?? true,
                v => { if (Plugin.DrawingColourOutline != null) Plugin.DrawingColourOutline.Value = v; },
                "On: outlines visible parts and sharp corners, hiding triangle seams and shallow facets. Off: draws the full wireframe on the colour sheet.");
            Toggle("Interior: silhouette", Plugin.DrawingSeeThroughOutline?.Value ?? true,
                v => { if (Plugin.DrawingSeeThroughOutline != null) Plugin.DrawingSeeThroughOutline.Value = v; },
                "On: adds only the vehicle's outer silhouette over the visible interior. Off: draws the full wireframe on the see-through sheet.");
        }

        Toggle("Show gun elevation", Plugin.DrawingElevation?.Value ?? false,
            v => { if (Plugin.DrawingElevation != null) Plugin.DrawingElevation.Value = v; },
            "Side view: dashed gun positions and angle labels at the gun's elevation and depression limits, from its laying drive. Does not cut away the tank.");
        Toggle("Show gun side traverse", Plugin.DrawingTraverse?.Value ?? false,
            v => { if (Plugin.DrawingTraverse != null) Plugin.DrawingTraverse.Value = v; },
            "Top view: shows independent left/right gun movement from its laying drive, useful for casemates. This does not include turret rotation.");
        Toggle("Show turret rotation", Plugin.DrawingTurretTraverse?.Value ?? false,
            v => { if (Plugin.DrawingTurretTraverse != null) Plugin.DrawingTurretTraverse.Value = v; },
            "Top view: shows turret movement using the traverse motor's Min and Max limits. An unrestricted turret gets one 360-degree arc.");
        if (gun != null)
        {
            string stored = Plugin.DrawingHiddenGunLimits?.Value ?? "[]";
            if (cachedLimits != stored) { gunLimits = new(stored); cachedLimits = stored; }
            string key = GunKey(gun);
            if (key.Length > 0)
                Toggle("Include this gun", gunLimits.Shows(key), show =>
                {
                    // Re-read here so a delayed inspector callback cannot overwrite another gun's choice.
                    var choices = new GunAnnotationPreferences(Plugin.DrawingHiddenGunLimits?.Value);
                    choices.Set(key, show);
                    if (Plugin.DrawingHiddenGunLimits != null) Plugin.DrawingHiddenGunLimits.Value = choices.Serialize();
                }, "Includes this gun in the movement annotations enabled above. Turning it off hides only its limit lines and labels; the gun stays visible. Remembered per design name and gun ID. Shared mounts use the largest included gun; other included guns can still annotate the same mount.");
        }

        bool blue = Plugin.DrawingBlue?.Value ?? false;
        Toggle("Save blue blueprint", blue,
            v => { if (Plugin.DrawingBlue != null) Plugin.DrawingBlue.Value = v; },
            "Saves an additional white-on-blue blueprint alongside the standard drawing sheets.");
        if (blue)
        {
            bool grid = Plugin.DrawingGrid?.Value ?? false;
            Toggle("Background grid", grid,
                v => { if (Plugin.DrawingGrid != null) Plugin.DrawingGrid.Value = v; },
                "Adds faint square grid lines behind the blue sheet. Each square is 0.25 m at drawing scale; every metre has a stronger line.");
            if (grid)
                ui.Slider("Grid strength (%)", Plugin.DrawingGridIntensity?.Value ?? 20, 0, 100,
                    Ui.FloatCallback(v => { if (Plugin.DrawingGridIntensity != null) Plugin.DrawingGridIntensity.Value = MathF.Round(v); }));
        }
    });
}
