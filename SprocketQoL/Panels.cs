using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Sprocket.UI;
using Sprocket.VehicleDesigner.Access;
using Sprocket.Vehicles.Cannons.Editor;
using Sprocket.Vehicles.Engines.Editor;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.Transmissions.Editor;
using Sprocket.Vehicles.Turrets.Editor;
using UnityEngine;
using VehicleDesigner.Compartments.Design;

namespace SprocketQoL;

/// Every QoL section of a part's panel, by the part editor it belongs to, in the order they show. The same sections
/// draw into the game's own panel (NativePanels) or, when that can't be hooked, QoL's fallback panel (QolPanel), so a
/// change to the game's panels costs the look, not the tools.
internal static class PanelSections
{
    sealed record Section(string Name, Func<Il2CppObjectBase, bool> Takes, Action<Il2CppObjectBase, Panel> Draw);
    static readonly List<Section> sections = new();

    static void Add<T>(string name, Action<T, Panel> draw) where T : Il2CppObjectBase =>
        sections.Add(new(name, e => e.TryCast<T>() != null, (e, p) => { if (e.TryCast<T>() is { } editor) draw(editor, p); }));

    /// Each registration on its own: one that names a part editor the game no longer has drops only that section.
    static void Try(string name, Action register)
    {
        try { register(); }
        catch (Exception ex) { Hooks.Failed("Panel sections", name, ex); }
    }

    internal static void Register()
    {
        // The order the sections were drawn in before (it was the order the tools attached).
        Try("Turret to Add-on (structure)", () => Add<PlateStructureEditor>("Turret to Add-on", InspectorSection.Structure));
        Try("Turret to Add-on (ring)", () => Add<TurretRingEditor>("Turret to Add-on", InspectorSection.Ring));
        Try("Turret drive repair (ring)", () => Add<TurretRingEditor>("Turret drive repair", TurretDriveRepair.Ring));
        Try("Turret drive repair (motor)", () => Add<TraverseMotorEditor>("Turret drive repair", TurretDriveRepair.Motor));
        Try("Merge and cut", () => Add<PlateStructureEditor>("Merge and cut", ShapeTools.Draw));
        Try("Restore", () => Add<PlateStructureEditor>("Restore", RestoreSection.Draw));
        Try("Hole quality", () => Add<PlateStructureEditor>("Hole quality", HoleQuality.Draw));
        Try("Mesh tools", () => Add<PlateStructureEditor>("Mesh tools", MeshTools.Draw));
        Try("Merge faces", () => Add<PlateStructureEditor>("Merge faces", MergeFaces.Draw));
        Try("Hotkeys", () => Add<PlateStructureEditor>("Hotkeys", (e, _) => Hotkeys.Seen(e)));
        Try("Gun length", () => Add<CannonEditor>("Gun length", GunLength.Draw));
        Try("Speed (transmission)", () => Add<TransmissionEditor>("Speed & acceleration", GearSpeeds.InTransmission));
        Try("Speed (engine)", () => Add<CombustionEngineComponentEditor>("Speed & acceleration", GearSpeeds.InEngine));
        Try("Own paint", () => Add<PlateStructureEditor>("Own paint", PartPaint.Draw));
        Try("Drawing sheet (structure)", () => Add<PlateStructureEditor>("Drawing sheet", DrawingSettings.Structure));
        Try("Drawing sheet (ring)", () => Add<TurretRingEditor>("Drawing sheet", DrawingSettings.Ring));
        Try("Drawing sheet (cannon)", () => Add<CannonEditor>("Drawing sheet", DrawingSettings.Cannon));
        Try("OBJ export / import", () => Add<PlateStructureEditor>("OBJ export / import", (_, p) => ObjTransfer.Inspector(p)));
    }

    /// Whether any QoL section belongs to this part editor.
    internal static bool Has(Il2CppObjectBase editor) => sections.Any(s => s.Takes(editor));

    /// Every QoL section of this part editor, drawn into `panel`.
    internal static void Draw(Il2CppObjectBase editor, Panel panel)
    {
        foreach (var s in sections) s.Draw(editor, panel);
    }

    /// The game's part editors open now (one per selected part type), from the editor itself: no panel needed.
    internal static List<Il2CppObjectBase> ActiveEditors()
    {
        var list = new List<Il2CppObjectBase>();
        try
        {
            var subs = DesignEditor.Instance?.Core?.Editor?.TryCast<IVehicleEditor>()?.SubEditors?.ActiveEditors;
            if (subs != null) foreach (var e in DesignEditor.Each(subs)) if (e != null) list.Add(e);
        }
        catch (Exception) { }
        return list;
    }

    internal static T? Active<T>() where T : Il2CppObjectBase
    {
        foreach (var e in ActiveEditors()) if (e.TryCast<T>() is { } t) return t;
        return null;
    }
}

/// The game's own part panels: one hook on each part editor's panel drawing, which draws its QoL sections into it.
/// The only place (with NativeInspectorBackend) that names the game's panel types.
[HarmonyPatch]
internal static class NativePanels
{
    /// When the game last drew a part panel with QoL sections into it (QolPanel's automatic mode watches this).
    internal static float LastDrawn = -1;

    static void Draw(Il2CppObjectBase editor, IGUILayout layout, Action redraw) => Ui.Guard("QoL panel sections", () =>
    {
        if (NativeInspectorBackend.Create(layout) is not { } backend) return;
        LastDrawn = Time.unscaledTime;
        PanelSections.Draw(editor, new Panel(backend, redraw));
    });

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditor), nameof(PlateStructureEditor.OnGUI))]
    static void Structure(PlateStructureEditor __instance, IGUILayout layout) => Draw(__instance, layout, __instance.RequestRedraw);

    [HarmonyPostfix, HarmonyPatch(typeof(TurretRingEditor), nameof(TurretRingEditor.OnGUI))]
    static void Ring(TurretRingEditor __instance, IGUILayout layout) => Draw(__instance, layout, __instance.RequestRedraw);

    [HarmonyPostfix, HarmonyPatch(typeof(TraverseMotorEditor), nameof(TraverseMotorEditor.OnGUI))]
    static void Motor(TraverseMotorEditor __instance, IGUILayout layout) => Draw(__instance, layout, __instance.RequestRedraw);

    [HarmonyPostfix, HarmonyPatch(typeof(CannonEditor), nameof(CannonEditor.OnGUI))]
    static void Cannon(CannonEditor __instance, IGUILayout layout) => Draw(__instance, layout, __instance.RequestRedraw);

    [HarmonyPostfix, HarmonyPatch(typeof(TransmissionEditor), nameof(TransmissionEditor.OnGUI))]
    static void Transmission(TransmissionEditor __instance, IGUILayout layout) => Draw(__instance, layout, __instance.RequestRedraw);

    [HarmonyPostfix, HarmonyPatch(typeof(CombustionEngineComponentEditor), nameof(CombustionEngineComponentEditor.OnGUI))]
    static void Engine(CombustionEngineComponentEditor __instance, IGUILayout layout) => Draw(__instance, layout, __instance.RequestRedraw);
}
