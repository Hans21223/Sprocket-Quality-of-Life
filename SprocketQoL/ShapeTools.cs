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
    static void Draw(PlateStructureEditor __instance, IGUILayout layout) => Ui.Inspector("Merge and cut", layout, () =>
    {
        var part = __instance.Component.VehicleObject;
        var editor = DesignEditor.Instance;
        var ui = Ui.Drawer(layout);
        if (editor == null || ui == null || part.GUID is not (Conversion.AddonGuid or Conversion.CompartmentGuid)) return;
        int addon = (int)part.VUID;
        bool body = part.GUID == Conversion.CompartmentGuid; // a turret or hull: add-ons can merge into it
        var others = editor.SelectedParts(Conversion.AddonGuid).Where(v => v != addon).ToList();
        if (body && others.Count == 0) return; // only offered when add-ons are selected with it
        Ui.Section(layout, body ? "Merge add-ons into this" : "Merge add-ons");
        if (others.Count == 0) ui.InfoField($"Select at least two add-ons. {Keybinds.Shown("join")} merges them into the last selected add-on.", 2);
        else
        {
            ui.InfoField($"{others.Count} selected add-on{(others.Count == 1 ? "" : "s")} will join this {(body ? "hull or turret" : "add-on")}. Ctrl+Z undoes the merge.", 2);
            var tip = new UITooltip("Merge into this part", body
                ? "Keeps the add-ons' position, shape and armour, and makes them part of this hull or turret. Their interior becomes part of this compartment. Mirrored copies merge too. Ctrl+Z undoes the merge."
                : "Keeps the add-ons' position, shape and armour. Attached parts move onto this add-on. When every selected add-on has a mirror partner, those partners merge too. Ctrl+Z undoes the merge.");
            ui.Button($"Merge {others.Count} add-on{(others.Count == 1 ? "" : "s")} into this", Ui.Callback(() =>
                editor.RequestLiveEdit("Merging add-ons", $"Merged {others.Count} add-on{(others.Count == 1 ? "" : "s")} into part {addon}.", json => AddonEdits.PlanMerge(json, addon, others, editor.LiveShapes(json)))), ref tip);
        }
        if (body) return; // cutting with a part is for add-ons

        Ui.Section(layout, "Cut with this add-on");
        var targets = editor.SelectedParts().Where(v => v != addon).ToList();
        string what = targets.Count == 0 ? "the shape it sits on" : $"{targets.Count} selected part{(targets.Count == 1 ? "" : "s")}";
        ui.InfoField($"Cuts this add-on's shape from {what}. The add-on must overlap the target. Ctrl+Z undoes the cut.", 3);
        ui.ToggleField("Keep cutting add-on", keepCutter, Ui.BoolCallback(v => keepCutter = v),
            "Off: removes this add-on after cutting. On: keeps it so you can move it and cut again.");
        ui.ToggleField("Rectangular border", rectangleBox, Ui.BoolCallback(v =>
        {
            rectangleBox = v;
            fill = v ? Fill.Mode.Rectangle : (fill == Fill.Mode.Rectangle ? Fill.Mode.Fewest : fill);
            __instance.RequestRedraw();
        }), "Adds a rectangular border around the opening to keep the surrounding face tidy. Off: connects the opening directly to the face's corners.");
        var fillTip = new UITooltip("Surrounding faces", "Click to cycle the layout around the cut. Fewest points uses only existing corners and the opening. Light fill adds a few points so no faces are long and thin. Smooth fill adds a ring of quads along the cut and more points for even faces. Rectangle box adds a rectangular border.");
        ui.Button($"Faces: {Fill.ModeNames[(int)fill]}", Ui.Callback(() =>
        {
            fill = (Fill.Mode)(((int)fill + 1) % Fill.ModeNames.Length);
            rectangleBox = (fill == Fill.Mode.Rectangle);
            __instance.RequestRedraw();
        }), ref fillTip);
        foreach (bool pocket in new[] { false, true })
        {
            var tip = new UITooltip(pocket ? "Cut recess" : "Cut through-hole", pocket
                ? "Makes a recess with walls and a floor, shaped by this add-on. The new plates inherit the add-on's armour. Ctrl+Z undoes the cut."
                : "Makes an opening through every plate this add-on overlaps. Mirrored plates are cut on both sides. Ctrl+Z undoes the cut.");
            var mode = rectangleBox ? Fill.Mode.Rectangle : fill;
            ui.Button(pocket ? "Cut recess (walls and floor)" : "Cut through-hole", Ui.Callback(() =>
                editor.RequestLiveEdit(pocket ? "Cutting a pocket" : "Cutting a hole", pocket ? "Pocket cut." : "Hole cut.",
                    json => AddonEdits.PlanCut(json, addon, targets, !keepCutter, pocket, mode, editor.LiveShapes(json)))), ref tip);
        }
    });

    static bool keepCutter;
    static bool rectangleBox;
    static Fill.Mode fill = Fill.Mode.Fewest;
}
