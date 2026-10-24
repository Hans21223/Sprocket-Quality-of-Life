using BepInEx.Configuration;

namespace SprocketQoL;

/// Optional additions to native picking and the hold-to-apply movement assistant.
internal static class MeshAssistSettings
{
    internal static ConfigEntry<bool>? HoverHighlight, CyclePicking, CrossPartTargets, EdgeSurfaceTargets, FaceSurfaceTargets;
    internal static void Load(ConfigFile config)
    {
        MeshSelection.LoadStorage(BepInEx.Paths.BepInExRootPath);
        HoverHighlight = config.Bind("Selection", "Highlight hovered target", true, "Draw a passive marker over the point or edge a click would choose.");
        CyclePicking = config.Bind("Selection", "Cycle overlapping targets", true, "The configured Cycle selection key chooses the next eligible point or edge near the pointer. X-ray still decides whether hidden geometry is eligible.");
        CrossPartTargets = config.Bind("Build assist", "Other part targets", false, "Suggest points, edges and surfaces on other plate structures in the current vehicle. Collected once at the start of a move; disable exploded view first.");
        EdgeSurfaceTargets = config.Bind("Build assist", "Closest edge points", true, "Include the closest point anywhere along stationary edges, in addition to optional edge midpoints.");
        FaceSurfaceTargets = config.Bind("Build assist", "Closest face points", false, "Include the closest point on stationary triangle and quad faces. Hold Apply prediction to use a suggestion; no automatic movement.");
    }
}
