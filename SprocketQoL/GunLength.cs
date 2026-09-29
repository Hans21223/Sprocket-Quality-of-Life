using HarmonyLib;
using Sprocket.UI;
using Sprocket.Vehicles.Cannons.Editor;

namespace SprocketQoL;

/// Firepower: shows the gun's length in calibers (L/xx) in the Cannon panel. L counts from the muzzle to the face of the
/// breech block, as guns are measured: the barrel plus the chamber, which holds the whole round. In the game the round is
/// its propellant plus a 3-caliber shell (CannonBlueprint.ShellLength), sitting behind the barrel segments, and
/// BoreLength is the barrel plus that round (the same 3 calibers the game fires: CannonConfig.ProjectileLength). The
/// barrel is the sum of its segments; a muzzle device is a separate part and isn't counted, as for real guns.
[HarmonyPatch]
public static class GunLength
{
    [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    static void Draw(CannonEditor __instance, IGUILayout layout) => Ui.Guard("Gun length", () =>
    {
        var gun = __instance.Component?.Blueprint;
        var ui = layout.TryCast<IGUIElementDrawer>();
        if (gun == null || ui == null || gun.Caliber == 0) return;
        int barrel = gun.BarrelLength, round = gun.ShellLength, propellant = gun.PropellantLength;
        Ui.Section(layout, "Gun length");
        ui.InfoField($"L/{Calibers(gun.BoreLength, gun.Caliber)}   ({gun.BoreLength} mm muzzle to breech face, {gun.Caliber} mm bore)\n" +
                     $"Barrel {barrel} mm (L/{Calibers(barrel, gun.Caliber)}) + chamber {round} mm\n" +
                     $"Round {round} mm: propellant {propellant} + shell {round - propellant} (3 calibers)\n" +
                     "A muzzle brake isn't counted, as for real guns", 4);
    });

    static string Calibers(int lengthMm, int caliberMm) => (lengthMm / (float)caliberMm).ToString("0.##");
}
