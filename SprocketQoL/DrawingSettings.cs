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
    static void Structure(IGUILayout layout) => Draw(layout);
    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(IGUILayout layout) => Draw(layout);
    [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    static void Cannon(CannonEditor __instance, IGUILayout layout) => Draw(layout, __instance.Component);

    internal static string GunKey(Cannon gun) => GunAnnotationPreferences.Key(gun.Vehicle?.DesignInfo?.Name, (int)gun.VehicleObject.VUID);

    static string? cachedLimits;
    static GunAnnotationPreferences gunLimits = new(null);

    static void Draw(IGUILayout layout, Cannon? gun = null) => Ui.Guard("Drawing sheet settings", () =>
    {
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (ui == null) return;
        Ui.Section(layout, "Drawing sheet (F9)");
        ui.InfoField("F9 saves the drawings.\nSettings are remembered.", 2);
        if (gun != null)
        {
            string stored = Plugin.DrawingHiddenGunLimits?.Value ?? "[]";
            if (cachedLimits != stored) { gunLimits = new(stored); cachedLimits = stored; }
            string key = GunKey(gun);
            if (key.Length > 0)
                ui.ToggleField("This gun's limits", gunLimits.Shows(key), Ui.BoolCallback(show =>
                {
                    // Re-read here so a delayed inspector callback cannot overwrite another gun's choice.
                    var choices = new GunAnnotationPreferences(Plugin.DrawingHiddenGunLimits?.Value);
                    choices.Set(key, show);
                    if (Plugin.DrawingHiddenGunLimits != null) Plugin.DrawingHiddenGunLimits.Value = choices.Serialize();
                }), "Include this gun in elevation, gun traverse and turret rotation annotations on F9 sheets. The gun itself stays visible. Global movement options must also be on. Remembered per design name and gun ID; renaming the design starts a separate selection. For shared mounts, the largest enabled gun supplies the limits.");
        }
        ui.ToggleField("No wireframe", Plugin.DrawingNoWireframe?.Value ?? false, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingNoWireframe != null) Plugin.DrawingNoWireframe.Value = v;
        }), "F9 colour and see-through drawings without any added lines, including outlines. Off by default; remembered between sessions. The separate lines-only drawing is unchanged.");
        ui.Slider("Intensity (%)", Plugin.DrawingIntensity?.Value ?? 100, 0, 100,
            Ui.FloatCallback(v => { if (Plugin.DrawingIntensity != null) Plugin.DrawingIntensity.Value = MathF.Round(v); }));
        ui.ToggleField("Gun elevation", Plugin.DrawingElevation?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingElevation != null) Plugin.DrawingElevation.Value = v; }),
            "Side view: dashed gun positions at the laying drive's limits, with angle labels. The tank stays intact.");
        ui.ToggleField("Gun traverse", Plugin.DrawingTraverse?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingTraverse != null) Plugin.DrawingTraverse.Value = v; }),
            "Top view: independent left/right gun traverse limits, useful for casemates. Uses the gun laying drive, not turret rotation.");
        ui.ToggleField("Turret rotation", Plugin.DrawingTurretTraverse?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingTurretTraverse != null) Plugin.DrawingTurretTraverse.Value = v; }),
            "Top view: rotation about the turret ring, using its traverse motor's Min and Max limits. Full rotation is shown as one 360-degree arc.");
        ui.ToggleField("Blue blueprint", Plugin.DrawingBlue?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingBlue != null) Plugin.DrawingBlue.Value = v; }),
            "Save an additional white-on-blue blueprint alongside the normal F9 drawings.");
        ui.ToggleField("Blueprint grid", Plugin.DrawingGrid?.Value ?? false,
            Ui.BoolCallback(v => { if (Plugin.DrawingGrid != null) Plugin.DrawingGrid.Value = v; }),
            "Add faint square grid lines behind the blue blueprint. Enable Blue blueprint too. Squares represent 0.25 m at drawing scale, with a stronger line every metre.");
        ui.Slider("Grid strength (%)", Plugin.DrawingGridIntensity?.Value ?? 20, 0, 100,
            Ui.FloatCallback(v => { if (Plugin.DrawingGridIntensity != null) Plugin.DrawingGridIntensity.Value = MathF.Round(v); }));
        ui.ToggleField("Colour outlines", Plugin.DrawingColourOutline?.Value ?? true, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingColourOutline != null) Plugin.DrawingColourOutline.Value = v;
        }), "F9 colour drawing: outline visible parts and sharp corners, suppressing triangle seams and shallow facets. On by default; turn off for wireframe. No wireframe hides all added lines in both coloured exports.");
        ui.ToggleField("Interior outline", Plugin.DrawingSeeThroughOutline?.Value ?? true, Ui.BoolCallback(v =>
        {
            if (Plugin.DrawingSeeThroughOutline != null) Plugin.DrawingSeeThroughOutline.Value = v;
        }), "F9 see-through drawing: highlight only the vehicle silhouette over the visible interior, with no mesh edges. On by default; turn off for wireframe. No wireframe hides all added lines in both coloured exports.");
    });
}
