using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.Turrets.Editor;

namespace SprocketQoL;

/// Turret to Add-on: a section in the game's own Turret Ring and Structure (turret body) panels.
/// Selecting the part is the game's normal selection, so the game highlights it.
[HarmonyPatch]
public static class InspectorSection
{
    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(TurretRingEditor __instance, IGUILayout layout) => Ui.Inspector("Turret to Add-on", layout, () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Structure(PlateStructureEditor __instance, IGUILayout layout) => Ui.Inspector("Turret to Add-on", layout, () =>
    {
        var parent = __instance.Component.VehicleTransform?.Parent?.GetComponent<VehicleObject>();
        if (parent != null && parent.GUID == Conversion.RingGuid) Draw(layout, (int)parent.VUID);
    });

    static void Draw(IGUILayout layout, int ringVuid)
    {
        var editor = DesignEditor.Instance;
        var ui = Ui.Drawer(layout);
        if (editor == null || ui == null) return;
        // This turret plus any other turrets selected alongside it, converted together in one reload.
        var rings = editor.SelectedTurretRings().Prepend(ringVuid).Distinct().ToList();
        Ui.Section(layout, "Turret to Add-on");
        ui.InfoField("Makes the turret a fixed add-on. Guns, crew and attached parts keep their positions. Loads an unsaved copy; Restore returns to the previous design.", 3);
        var tip = new UITooltip("Make a fixed add-on",
            "Removes the turret ring and traverse motor, keeping the body as a fixed add-on. Select several turrets to convert them together. Mirror partners convert too. The original design is backed up; save the new copy to keep the result.");
        string label = rings.Count == 1 ? "Convert turret to add-on" : $"Convert {rings.Count} turrets to add-ons";
        ui.Button(label, Ui.Callback(() => editor.RequestEdit(rings.Count == 1 ? "Converting turret to add-on" : $"Converting {rings.Count} turrets to add-ons",
            $"{(rings.Count == 1 ? "Turret" : $"{rings.Count} turrets")} converted to add-ons. Save under the new name to keep it.", json =>
            {
                var bodies = new List<int>();
                // A turret's mirror twin converts with it, so the pair stays alike.
                var objects = Conversion.Objects(Conversion.Parse(json));
                var all = rings.ToList();
                foreach (int ring in rings)
                    if (objects.TryGetValue(ring, out var r) && r["transform"]?["mirrorVuid"]?.GetValue<int>() is int t && !all.Contains(t)
                        && objects.TryGetValue(t, out var twin) && Conversion.GuidOf(twin) == Conversion.RingGuid && twin["transform"]?["mirrorVuid"]?.GetValue<int>() == ring)
                        all.Add(t);
                foreach (int ring in all)
                {
                    var r = Conversion.Convert(json, ring);
                    json = r.Json;
                    bodies.Add(r.BodyId);
                }
                return new EditResult(json, bodies[^1], $"rings={string.Join(",", all)} -> add-on bodies={string.Join(",", bodies)}");
            })), ref tip);
    }
}
