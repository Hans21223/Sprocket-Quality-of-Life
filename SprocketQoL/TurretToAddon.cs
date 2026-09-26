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
    static void Ring(TurretRingEditor __instance, IGUILayout layout) => Ui.Guard("Turret to Add-on", () =>
        Draw(layout, (int)__instance.Component.VehicleObject.VUID));

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Structure(PlateStructureEditor __instance, IGUILayout layout) => Ui.Guard("Turret to Add-on", () =>
    {
        var parent = __instance.Component.VehicleTransform?.Parent?.GetComponent<VehicleObject>();
        if (parent != null && parent.GUID == Conversion.RingGuid) Draw(layout, (int)parent.VUID);
    });

    static void Draw(IGUILayout layout, int ringVuid)
    {
        var editor = DesignEditor.Instance;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (editor == null || ui == null) return;
        // This turret plus any other turrets selected alongside it, converted together in one reload.
        var rings = editor.SelectedTurretRings().Prepend(ringVuid).Distinct().ToList();
        Ui.Section(layout, "Turret to Add-on");
        ui.InfoField("Turns this turret into a fixed add-on structure.\nGuns, crew and attached parts stay where they are.", 2);
        var tip = new UITooltip("Convert to add-on",
            "Removes the turret ring and traverse motor and keeps the body as an add-on structure. " +
            "Select several turrets to convert them all at once. The result loads as a new unsaved copy; the design before is backed up.");
        string label = rings.Count == 1 ? "Convert to add-on" : $"Convert {rings.Count} selected turrets to add-ons";
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
