namespace SprocketQoL;

public enum ObjExportCategory
{
    Exterior, Engine, Powertrain, Transmission, Ammunition, InternalFuel, Crew, Sights, TurretMotors, LayingDrives
}

/// Part registry component IDs classify physical parts independently of their user-defined name.
public static class ObjExportPolicy
{
    public static readonly IReadOnlyDictionary<string, (string Name, ObjExportCategory Category)> KnownParts =
        new Dictionary<string, (string Name, ObjExportCategory Category)>(StringComparer.OrdinalIgnoreCase)
    {
        ["ebcf5eda-e50b-4f18-a063-0138a9d630e0"] = ("Engine", ObjExportCategory.Engine),
        ["5081f19f-4029-4686-9b92-57261096a362"] = ("Clutch braking powertrain", ObjExportCategory.Powertrain),
        ["6192f19f-4029-4686-9b92-57261096a362"] = ("Twin transmission powertrain", ObjExportCategory.Powertrain),
        ["fbbe2fd4-9092-46c6-8657-8d5fbcd8186d"] = ("Steering mechanism", ObjExportCategory.Powertrain),
        ["7a508b04-0d69-40d3-9f4c-92c02346b9de"] = ("Longitudinal transmission", ObjExportCategory.Transmission),
        ["6b417b04-0d69-40d3-9f4c-92c02346b9de"] = ("Transverse transmission", ObjExportCategory.Transmission),
        ["85b83f8c-b64b-43e4-8785-24cde9fccc1d"] = ("Ammunition rack", ObjExportCategory.Ammunition),
        ["5e8ab5c7-e9f1-4c64-a04a-29efc78b1918"] = ("Internal fuel tank", ObjExportCategory.InternalFuel),
        ["ecd3341c-f605-4816-946c-591eaa7e4f7d"] = ("External fuel barrel", ObjExportCategory.Exterior),
        ["4481135e-cefd-499c-8122-414942eecbe4"] = ("Crew position", ObjExportCategory.Crew),
        ["f90f373f-4edb-4563-a162-6eee6a5d77dd"] = ("Gunner's sight", ObjExportCategory.Sights),
        ["147d4042-4a13-4477-9adf-12e8291481e0"] = ("Turret traverse motor", ObjExportCategory.TurretMotors),
        ["0c396ede-f39b-46ac-9d7e-e37c22b2c89e"] = ("Gun laying drive", ObjExportCategory.LayingDrives),
        ["ef551781-05bc-4aff-902a-372bdf0b3621"] = ("Track assembly", ObjExportCategory.Exterior)
    };

    public static bool DefaultIncluded(ObjExportCategory category) => category == ObjExportCategory.Exterior;

    public static ObjExportCategory Classify(string? guid, IEnumerable<string?> componentIds, bool isInternal)
    {
        var components = componentIds.Where(c => c != null).ToHashSet(StringComparer.Ordinal);
        if (components.Contains("combustionEngine")) return ObjExportCategory.Engine;
        if (components.Contains("powertrain") || components.Contains("powertrainSteering")) return ObjExportCategory.Powertrain;
        if (components.Contains("transmission")) return ObjExportCategory.Transmission;
        if (components.Contains("ammoRack") || components.Contains("ammoRackModel")) return ObjExportCategory.Ammunition;
        if (components.Contains("fuelTank") && isInternal) return ObjExportCategory.InternalFuel;
        if (components.Contains("crewSeat") || components.Contains("postureUnityArmature")) return ObjExportCategory.Crew;
        if (components.Contains("sight")) return ObjExportCategory.Sights;
        if (components.Contains("traverseMotor") || components.Contains("traverseMotorModel")) return ObjExportCategory.TurretMotors;
        if (components.Contains("layingDrive")) return ObjExportCategory.LayingDrives;
        return KnownParts.TryGetValue(guid ?? "", out var known) ? known.Category : ObjExportCategory.Exterior;
    }
}
