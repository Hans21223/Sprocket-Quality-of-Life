using Sprocket.UI;
using Sprocket.Vehicles.Tracks.Editor;

namespace SprocketQoL;

/// Tracks: how many links each track has and how long that is, in the Tracks panel.
internal static class TrackLength
{
    internal static void Draw(TrackEditor __instance, Panel layout) => Ui.Inspector("Track length", layout, () =>
    {
        var belt = __instance.selectedBelt;
        var ui = Ui.Drawer(layout);
        if (belt == null || ui == null || !RuntimeMetrics.TryTrackLength(belt.SegmentCount, belt.SegmentPitch, out double length)) return;
        Ui.Section(layout, "Track length");
        ui.InfoField($"{belt.SegmentCount} links of {belt.SegmentPitch * 1000d:0.#} mm: {length:0.00} m a side", 1);
    });
}
