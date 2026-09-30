using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.Turrets.Editor;
using VehicleDesigner.Compartments.Design;

namespace SprocketQoL;

[HarmonyPatch]
public static class TurretDriveRepair
{
    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(TurretRingEditor __instance, IGUILayout layout) => Ui.Inspector("Turret drive repair", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    [HarmonyPostfix, HarmonyPatch(typeof(TraverseMotorEditor), nameof(TraverseMotorEditor.OnGUI))]
    static void Motor(TraverseMotorEditor __instance, IGUILayout layout) => Ui.Inspector("Turret drive repair", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    static void Draw(IGUILayout layout, int focus)
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null) return;
        Ui.Section(layout, "Mirrored turret drives");
        ui.InfoField("Fix mirror twins that share one drive.\nEach turret reconnects to its own motor.", 2);
        var tip = new UITooltip("Fix mirrored turret drives", "Checks the vehicle's mirrored turret pairs and reconnects each to its own attached motor. " +
            "Pairs with missing or ambiguous motors are left alone. Loads an unsaved result and backs up the design; Restore undoes the repair.");
        ui.Button("Fix mirrored turret drives", Ui.Callback(() => editor.RequestEdit("Repairing mirrored turret drives",
            "Mirrored turret drives repaired. Restore undoes it.", json =>
            {
                var result = Conversion.RepairMirroredTurretDrives(json);
                if (result.Repaired == 0) throw new Exception(result.Unresolved > 0
                    ? "No drive repaired: a mirror pair has missing or ambiguous attached motors."
                    : "No crossed or shared drive connections found on mirrored turret pairs.");
                return new EditResult(result.Json, focus, $"{result.Repaired} ring drive references repaired; {result.Unresolved} ambiguous pairs left unchanged");
            })), ref tip);
        if (editor.CanRestore && editor.LastEditedPart == focus)
        {
            var restore = new UITooltip("Restore", "Reloads the design as it was before the last drive repair.");
            ui.Button("Restore design before last edit", Ui.Callback(editor.RequestRestore), ref restore);
        }
    }
}
