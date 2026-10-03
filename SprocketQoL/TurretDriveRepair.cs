using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.Turrets.Editor;
using VehicleDesigner.Compartments.Design;

namespace SprocketQoL;

[HarmonyPatch]
public static class TurretDriveRepair
{
    internal static void Ring(TurretRingEditor __instance, Panel layout) => Ui.Inspector("Turret drive repair", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    internal static void Motor(TraverseMotorEditor __instance, Panel layout) => Ui.Inspector("Turret drive repair", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    static void Draw(Panel layout, int focus)
    {
        var editor = DesignEditor.Instance;
        var ui = Ui.Drawer(layout);
        if (editor == null || ui == null) return;
        Ui.Section(layout, "Mirrored turret drives");
        ui.InfoField("Reconnects mirrored turrets to their own motors. Checks every mirrored pair in the vehicle. Loads an unsaved copy; Restore undoes the repair.", 3);
        var tip = new Tip("Repair drive connections", "Fixes mirrored turrets sharing a motor or connected to the wrong partner's motor. Requires each turret to have its own attached motor; uncertain pairs stay unchanged. Backs up the original design and loads an unsaved result.");
        ui.Button("Repair mirrored drives", Ui.Callback(() => editor.RequestEdit("Repairing mirrored turret drives",
            "Mirrored turret drives repaired. Restore undoes it.", json =>
            {
                var result = Conversion.RepairMirroredTurretDrives(json);
                if (result.Repaired == 0) throw new Exception(result.Unresolved > 0
                    ? "No drive repaired: a mirror pair has missing or ambiguous attached motors."
                    : "No crossed or shared drive connections found on mirrored turret pairs.");
                return new EditResult(result.Json, focus, $"{result.Repaired} ring drive references repaired; {result.Unresolved} ambiguous pairs left unchanged");
            })), tip);
        if (editor.CanRestore && editor.LastEditedPart == focus)
        {
            var restore = new Tip("Restore previous design", "Reloads the saved snapshot from before the last Quality of Life edit. Changes made since that snapshot are discarded.");
            ui.Button("Restore previous design", Ui.Callback(editor.RequestRestore), restore);
        }
    }
}
