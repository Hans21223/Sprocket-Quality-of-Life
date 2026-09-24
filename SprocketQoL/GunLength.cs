using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons.Editor;

namespace SprocketQoL;

/// Firepower: shows the gun's length in calibers (L/xx) in the Cannon panel.
[HarmonyPatch]
public static class GunLength
{
    [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    static void Draw(CannonEditor __instance, IGUILayout layout) => Ui.Guard("Gun length", () =>
    {
        var gun = __instance.Component?.Blueprint;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (gun == null || ui == null || gun.Caliber == 0) return;
        Ui.Section(layout, "Gun length");
        ui.InfoField($"Barrel  L/{Calibers(gun.BarrelLength, gun.Caliber)}   ({gun.BarrelLength} mm, {gun.Caliber} mm bore)\n" +
                     $"Bore  L/{Calibers(gun.BoreLength, gun.Caliber)}   ({gun.BoreLength} mm)", 2);
    });

    static string Calibers(int lengthMm, int caliberMm) => (lengthMm / (float)caliberMm).ToString("0.#");
}
