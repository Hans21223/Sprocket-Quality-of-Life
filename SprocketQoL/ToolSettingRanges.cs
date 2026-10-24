namespace SprocketQoL;

/// The values each numeric tool preference may take: a config file or preset with anything else gets the default back.
internal static class ToolSettingRanges
{
    internal static bool Valid(string field, double number)
    {
        (double min, double max) range = field switch
        {
            "segments" => (4, 96), "sizePercent" => (10, 300),
            "insetMm" or "bevelMm" or "smoothMm" or "filletRadiusMm" => (1, 500),
            "smoothSegments" or "filletSegments" or "splitSections" => (2, 16),
            "loopCuts" => (1, 16), "loopOffset" => (1, 99), "filletControlMm" => (.1, 20),
            "flatAngle" => (.5, 30), "radiusMm" => (10, 5000), "bridgeCuts" => (0, 32),
            "bridgeSmooth" => (0, 200), "mirrorMm" => (.5, 50), "exactLengthMm" => (1, 10000),
            "slideAmount" => (1, 500), "clothCell" => (.05, .25), "clothStiffness" => (.02, 1),
            "netHole" => (.4, .85), "netHoleMm" => (2, 200), "scatterCount" => (1, 300), "count" => (1, 100), "axis" => (0, 2),
            "radius" => (0, 10000), "sweep" => (-3600, 3600),
            "holeFill" => (0, Fill.ModeNames.Length), // an index into Create Hole's fills: Fill's modes, then the game's own fan
            _ => (-100000, 100000)
        };
        return double.IsFinite(number) && number >= range.min && number <= range.max;
    }
}
