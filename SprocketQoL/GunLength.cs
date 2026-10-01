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
    static void Draw(CannonEditor __instance, IGUILayout layout) => Ui.Inspector("Gun length", layout, () =>
    {
        var gun = __instance.Component?.Blueprint;
        var ui = Ui.Drawer(layout);
        if (gun == null || ui == null || gun.Caliber == 0) return;
        int barrel = gun.BarrelLength, round = gun.ShellLength, propellant = gun.PropellantLength;
        Ui.Section(layout, "Gun length");
        ui.InfoField($"Total length: {gun.BoreLength:N0} mm (L/{Calibers(gun.BoreLength, gun.Caliber)})\n" +
                     $"Barrel: {barrel:N0} mm | Chamber: {round:N0} mm\n" +
                     $"Calibre: {gun.Caliber:N0} mm\n" +
                     $"Round: {propellant:N0} mm propellant + {round - propellant:N0} mm shell\n" +
                     "L/ is total length divided by calibre. The muzzle brake is excluded.", 5);
    });

    static string Calibers(int lengthMm, int caliberMm) => caliberMm > 0 ? (lengthMm / (float)caliberMm).ToString("0.##") : "0";
}
