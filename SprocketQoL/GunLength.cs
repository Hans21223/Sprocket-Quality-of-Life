using HarmonyLib;
using Sprocket.ProjectilePhysicsSystems;
using Sprocket.UI;
using Sprocket.Vehicles;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.WeaponFramework;

namespace SprocketQoL;

/// Firepower: shows the gun's length in calibers (L/xx) in the Cannon panel. L counts from the muzzle to the face of the
/// breech block, as guns are measured: the barrel plus the chamber, which holds the whole round. In the game the round is
/// its propellant plus a 3-caliber shell (CannonBlueprint.ShellLength), sitting behind the barrel segments, and
/// BoreLength is the barrel plus that round (the same 3 calibers the game fires: CannonConfig.ProjectileLength). The
/// barrel is the sum of its segments; a muzzle device is a separate part and isn't counted, as for real guns.
[HarmonyPatch]
public static class GunLength
{
    internal static void Draw(CannonEditor __instance, Panel layout) => Ui.Inspector("Gun length", layout, () =>
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

    internal static string Calibers(int lengthMm, int caliberMm) => caliberMm > 0 ? (lengthMm / (float)caliberMm).ToString("0.##") : "0";

    /// The reload from each ammo rack the gun loads from, by the game's own loader rules (the times its rack labels show:
    /// pickup, carrying and ramming, one loader), the shots a minute from the nearest rack, and the projectile mass fired a
    /// minute: a stand-in for damage per minute, which the game has no figure for.
    internal static void DrawReload(CannonEditor __instance, Panel layout) => Ui.Inspector("Reload", layout, () =>
    {
        var gun = __instance.Component?.Blueprint;
        var ui = Ui.Drawer(layout);
        if (gun == null || ui == null || gun.Caliber <= 0 || gun.Caliber > ushort.MaxValue
            || gun.PropellantLength < 0 || gun.PropellantLength > ushort.MaxValue || gun.ShellLength <= 0) return;
        float projectile = ShellProperties.GetProjectileMass((ushort)gun.Caliber);
        float propellant = ShellProperties.GetPropellantMass((ushort)gun.Caliber, (ushort)gun.PropellantLength);
        if (!float.IsFinite(projectile) || projectile <= 0 || !float.IsFinite(propellant) || propellant < 0) return;
        var info = new ShellLoadInfo(projectile, propellant, gun.ShellLength / 1000f);
        var technique = LoaderRules.GetLoadTechnique(ref info);
        var times = new List<(float Total, float Travel, float Distance)>();
        bool hasRack = false;
        foreach (var path in __instance.availableRacks ?? new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<VehicleComponentPath>(0))
        {
            if (path == null) continue;
            hasRack = true;
            if (path.RequiresUpdate) path.Update();
            float distance = path.Distance;
            if (!float.IsFinite(distance) || distance < 0) continue;
            float total = LoaderRules.CalculateLoadTime(technique, distance, ref info);
            float travel = LoaderRules.CalculateTravelTime(technique, distance, ref info);
            if (RuntimeMetrics.ValidReload(total, travel, distance, projectile)) times.Add((total, travel, distance));
        }
        Ui.Section(layout, "Reload");
        if (times.Count == 0) { ui.InfoField(hasRack ? "Reload timing is unavailable while the gun and racks update." : "No ammo rack this gun can load from yet.", 1); return; }
        var fast = times.MinBy(t => t.Total);
        var slow = times.MaxBy(t => t.Total);
        double perMinute = 60d / fast.Total;
        ui.InfoField($"Nearest rack: {fast.Total:0.00} s ({fast.Travel:0.00} s carrying, {fast.Distance:0.00} m)\n" +
                     (times.Count > 1 ? $"Furthest rack: {slow.Total:0.00} s ({slow.Distance:0.00} m)\n" : "") +
                     $"{perMinute:0.#} shots a minute, {perMinute * projectile:0.#} kg of shell a minute\n" +
                     "One loader, timed as the rack labels are.", times.Count > 1 ? 4 : 3);
    });
}
