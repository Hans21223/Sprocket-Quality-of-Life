using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;

namespace SprocketQoL;

/// Add-on panel: "Merge add-ons" folds the other selected add-ons into this one, and "Cut with this add-on" cuts the
/// add-on's shape out of the structure under it (a Boolean cut).
/// (Round add-ons are palette parts in the "Round Add-on Parts" data mod, placed like the game's cube.)
[HarmonyPatch]
public static class ShapeTools
{
    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Merge and cut", () =>
    {
        var part = __instance.Component.VehicleObject;
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null || part.GUID is not (Conversion.AddonGuid or Conversion.CompartmentGuid)) return;
        int addon = (int)part.VUID;
        bool body = part.GUID == Conversion.CompartmentGuid; // a turret or hull: add-ons can merge into it
        var others = editor.SelectedParts(Conversion.AddonGuid).Where(v => v != addon).ToList();
        if (body && others.Count == 0) return; // only offered when add-ons are selected with it
        Ui.Section(layout, body ? "Merge add-ons into this" : "Merge add-ons");
        if (others.Count == 0) ui.InfoField("Select other add-ons too to merge them into this one.\nCtrl+J merges all selected into the last one selected.", 2);
        else
        {
            var tip = new UITooltip("Merge add-ons", body
                ? "The selected add-ons become part of this turret or hull: same shape, place and armour, and they turn with it. Their inside counts as this part's inside. Mirror twins come along."
                : "The other selected add-ons become part of this one: same shape, place and armour. Parts attached to them move onto this one. If this add-on and the others all have mirror twins, the twins merge the same way.");
            ui.Button($"Merge {others.Count} selected add-on{(others.Count == 1 ? "" : "s")} into this one", Ui.Callback(() =>
                editor.RequestLiveEdit("Merging add-ons", $"Merged {others.Count} add-on{(others.Count == 1 ? "" : "s")} into part {addon}.", json => AddonEdits.PlanMerge(json, addon, others))), ref tip);
        }
        if (body) return; // cutting with a part is for add-ons

        Ui.Section(layout, "Cut with this add-on");
        var targets = editor.SelectedParts().Where(v => v != addon).ToList();
        string what = targets.Count == 0 ? "the shape it sits on" : $"{targets.Count} selected part{(targets.Count == 1 ? "" : "s")}";
        // Explicit line breaks: the panel shows exactly the lines it's told, and cuts off the rest.
        ui.InfoField($"Cuts this add-on's shape out of {what}.\nHole goes through; pocket adds walls and floor.\nCtrl+Z undoes it.", 3);
        // Short labels: a toggle's label only gets the narrow left column.
        ui.ToggleField("Keep add-on", keepCutter, Ui.BoolCallback(v => keepCutter = v),
            "Off: the cutting add-on is removed. On: it stays (e.g. to cut again elsewhere).");
        ui.ToggleField("Smooth fill", smoothFill, Ui.BoolCallback(v => smoothFill = v),
            "Off (light): one ring of points between the hole and the face's corners, few points to edit. " +
            "On (smooth): a ring of quads hugging the hole, then rings stepping out (more points, even slices).");
        foreach (bool pocket in new[] { false, true })
        {
            var tip = new UITooltip(pocket ? "Cut pocket" : "Cut hole", pocket
                ? "Cuts a recess the add-on's shape: its surface inside the structure becomes plates with its armour."
                : "Cuts a hole the add-on's shape through every plate it passes through.");
            ui.Button(pocket ? "Cut pocket (with walls)" : "Cut hole", Ui.Callback(() =>
                editor.RequestLiveEdit(pocket ? "Cutting a pocket" : "Cutting a hole", pocket ? "Pocket cut." : "Hole cut.",
                    json => AddonEdits.PlanCut(json, addon, targets, !keepCutter, pocket, light: !smoothFill))), ref tip);
        }
    });

    static bool keepCutter, smoothFill;
}
