namespace SprocketQoL;

/// A gun belongs to its closest trunnions, never to every ancestor mount in the vehicle.
public static class DrawingMounts
{
    public const string TrunnionGuid = "e11ce433-c0b3-4e52-899f-898bfe6d3fd3";
    public record Part(int Parent, bool Trunnion, bool Turret);

    public static int Owner(int gun, IReadOnlyDictionary<int, Part> parts)
    {
        var visited = new HashSet<int>();
        for (int id = gun; id >= 0 && visited.Add(id) && parts.TryGetValue(id, out var part); id = part.Parent)
        {
            if (part.Trunnion) return id;
            // A directly mounted turret gun must not borrow a mount outside its own turret.
            if (part.Turret) return -1;
        }
        return -1;
    }

    public static int? SelectGun(int mount, IEnumerable<(int Id, int Caliber)> guns,
        IReadOnlyDictionary<int, Part> parts, Func<int, bool> included)
    {
        if (mount < 0) return null;
        int? best = null;
        int caliber = int.MinValue;
        foreach (var gun in guns)
            if (included(gun.Id) && Owner(gun.Id, parts) == mount &&
                (!best.HasValue || gun.Caliber > caliber || gun.Caliber == caliber && gun.Id < best.Value))
            {
                best = gun.Id;
                caliber = gun.Caliber;
            }
        return best;
    }
}
