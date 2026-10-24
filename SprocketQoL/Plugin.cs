using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.UI;
using UnityEngine.Events;

namespace SprocketQoL;

/// Quality of Life: small editor improvements, each one a section in the game's own inspector panels.
[BepInPlugin("local.sprocket.qol", "Quality of Life", "1.10.0")]
[BepInDependency(ModApi.Guid, BepInDependency.DependencyFlags.SoftDependency)] // Sprocket Mod API, if installed (ModApi.cs)
public sealed class Plugin : BasePlugin
{
    internal static ManualLogSource ModLog = null!;
    internal static ConfigEntry<string>? Folded;
    internal static ConfigEntry<bool>? ShowHotkeys;
    internal static ConfigEntry<bool>? PartMassMarkers;
    internal static ConfigEntry<bool>? DrawingNoWireframe;
    internal static ConfigEntry<bool>? DrawingSeeThroughOutline;
    internal static ConfigEntry<bool>? DrawingColourOutline;
    internal static ConfigEntry<float>? DrawingIntensity;
    internal static ConfigEntry<bool>? DrawingElevation, DrawingTraverse, DrawingBlue, DrawingTurretTraverse;
    internal static ConfigEntry<bool>? DrawingGrid;
    internal static ConfigEntry<float>? DrawingGridIntensity;
    internal static ConfigEntry<string>? DrawingHiddenGunLimits;
    internal static ConfigEntry<float>? ExplodeSpread, FlashlightPercent, FullbrightPercent;
    internal static ConfigEntry<int>? BackupsKept;
    internal static ConfigEntry<float>? RotationSnap;
    internal static ConfigEntry<bool>? MirrorMerge, RelativeSnap;
    internal static ConfigEntry<bool>? BetterMeshPicking;
    internal static ConfigEntry<float>? MeshPickRadius;
    internal static ConfigEntry<bool>? PredictiveBuild, PredictEdgeMidpoints, PredictFaceCentres;
    internal static ConfigEntry<float>? PredictionRadius;
    internal static ConfigEntry<bool>? RecordDrives, StopNaNs;
    internal static ConfigEntry<string>? PanelMode, PanelPosition, ShortcutsPosition;
    internal static ConfigEntry<string>? PhotoResolution, PhotoMethod;
    internal static ConfigEntry<string>? MenuOpens, MenuHidden;
    public override void Load()
    {
        ModLog = Log;
        PhotoResolution = Config.Bind("Photo", "Resolution", "Screen", new ConfigDescription(
            "F8 photo long edge: Screen, 2K (2560), 4K (3840), 6K (5760) or 8K (7680). Keeps the camera aspect ratio.",
            new AcceptableValueList<string>(PhotoOutput.Resolutions)));
        PhotoMethod = Config.Bind("Photo", "Capture method", "Render", new ConfigDescription(
            "Render draws the scene at the selected resolution. Upscale resizes the finished screenshot without adding scene detail.",
            new AcceptableValueList<string>(PhotoOutput.Methods)));
        DrawingNoWireframe = Config.Bind("Drawing sheet", "No wireframe", false, "Omit the wireframe overlay from F9 colour and see-through exports. The separate lines-only drawing is unchanged.");
        DrawingSeeThroughOutline = Config.Bind("Drawing sheet", "See-through outline only", true, "Use only the vehicle silhouette over the see-through interior, instead of mesh edges. No wireframe disables this outline too.");
        DrawingColourOutline = Config.Bind("Drawing sheet", "Colour outline only", true, "Use clean visible part contours and sharp corners on the colour drawing, without mesh triangulation. No wireframe disables these outlines too.");
        DrawingIntensity = Config.Bind("Drawing sheet", "Wireframe intensity percent", 100f, "Strength of geometry lines on F9 sheets, from 0 to 100. Paint, dimensions and text keep their strength.");
        DrawingElevation = Config.Bind("Drawing sheet", "Gun elevation and depression", false, "Show dashed gun positions at the laying drive's elevation and depression limits in the side view. Keeps the vehicle intact.");
        DrawingTraverse = Config.Bind("Drawing sheet", "Gun traverse", false, "Show dashed gun positions at the laying drive's left and right traverse limits in the top view (including casemates). Keeps the vehicle intact.");
        DrawingTurretTraverse = Config.Bind("Drawing sheet", "Turret rotation", false, "Show turret rotation in the top view using the traverse motor minimum and maximum angles. Full rotation is shown as a single 360-degree arc.");
        DrawingBlue = Config.Bind("Drawing sheet", "Blue blueprint", false, "Also save a white-on-blue line drawing, with the same dimensions and gun movement options.");
        DrawingGrid = Config.Bind("Drawing sheet", "Blueprint grid", false, "Add a faint square background grid to the blue blueprint only. Minor squares are 0.25 m at the drawing scale, with a stronger line every metre.");
        DrawingGridIntensity = Config.Bind("Drawing sheet", "Grid intensity percent", 20f, "Strength of the blue blueprint background grid, from 0 to 100. Vehicle lines and text are drawn over it.");
        DrawingHiddenGunLimits = Config.Bind("Drawing sheet", "Hidden gun limits", "[]", "Guns excluded from movement-limit annotations only. Managed by This gun's limits in the cannon panel. Stored per design name and gun ID; renaming a design starts a separate selection.");
        Folded = Config.Bind("Panels", "Folded sections", "", "Quality of Life sections folded away in the editor panels, separated by |");
        ShowHotkeys = Config.Bind("Panels", "Show hotkeys box", true, "The Hotkeys box beside a hand-made structure's panel (F1 in the editor shows or hides it)");
        PartMassMarkers = Config.Bind("Panels", "Show part mass markers", true, "Show the blue mass marker for each part when COM is enabled in the editor view filters. Disabling COM hides both vehicle and part mass markers.");
        ExplodeSpread = Config.Bind("Panels", "Exploded view spread", 0.5f, "How far apart the exploded view (F2) moves parts, in metres (F3 / F4 change it)");
        FlashlightPercent = Config.Bind("Panels", "Flashlight brightness", 80f, "How bright the flashlight (F6) is where it lands, in percent of the sun");
        FullbrightPercent = Config.Bind("Panels", "Fullbright brightness", 25f, "How bright each of fullbright's (F7) 14 lights is, in percent of the sun");
        RotationSnap = Config.Bind("Editor", "Rotation snap (degrees)", 0f, "While the game's rotation snap is on, turns snap in steps of this many degrees instead of the game's own (7.5 makes a 48-sided circle, 5 a 72-sided one). 0 keeps the game's step.");
        MirrorMerge = Config.Bind("Editor", "Mirror merge", true, "With the editor's Mirror on, Merge (M) merges the mirrored points on the other side too.");
        RelativeSnap = Config.Bind("Editor", "Relative snap", false, "Snapping a move (holding the snap key, normally Ctrl) moves the points in whole grid steps from where they start, instead of putting each point on the grid, so a shape whose points are off the grid keeps its shape. Off: the game's own snap.");
        BetterMeshPicking = Config.Bind("Selection", "Better point and edge picking", true,
            "Pick the nearest visible vertex or edge within a fixed screen-pixel radius. Avoids selecting distant corners across a face. Keeps the game's selection modifiers and X-ray mode. Off restores the game's picker.");
        MeshPickRadius = Config.Bind("Selection", "Click radius (pixels)", 12f, new ConfigDescription(
            "Maximum distance from the cursor to a vertex or edge, in screen pixels. Stays consistent when zooming. Smaller is more precise; larger is easier to click.",
            new AcceptableValueRange<float>(4, 30)));
        PredictiveBuild = Config.Bind("Build assist", "Advanced predictive mode", false,
            "Suggest nearby movement targets while moving points, edges or faces of a hand-made structure. Shows a guide; hold the Apply prediction key (N by default) to use the target during the current move. No movement is applied automatically.");
        PredictionRadius = Config.Bind("Build assist", "Suggestion radius (pixels)", 14f, new ConfigDescription(
            "Screen distance within which a nearby target is suggested. Smaller values are more precise; larger values are easier to reach.", new AcceptableValueRange<float>(4, 40)));
        PredictEdgeMidpoints = Config.Bind("Build assist", "Suggest edge midpoints", true, "Include the middle of stationary edges as possible movement targets.");
        PredictFaceCentres = Config.Bind("Build assist", "Suggest face centres", true, "Include the centre of stationary faces as possible movement targets.");
        BackupsKept = Config.Bind("Backups", "Backups kept", 50, "How many design backups (BepInEx\\SprocketQoLBackups, one per edit) to keep; the oldest go first. 0 keeps them all.");
        StopNaNs = Config.Bind("Graphics", "Black box fix", true, "Turns on the cameras' Stop NaNs: on some graphics cards (older ones and old drivers most), smoke and fire leave bad pixels that bloom and anti-aliasing smear into black boxes. Costs a little GPU time.");
        RecordDrives = Config.Bind("Diagnostics", "Record drives", false, "Write every physics step of your own vehicle's drivetrain to BepInEx\\SprocketQoL-drives while you drive (a few MB a minute), to check the Speed & acceleration figures against the game.");
        PanelMode = Config.Bind("Panels", "QoL panel", "Automatic", "Quality of Life's own part panel: Automatic (only when the game's part panels can't be drawn into, after a game update), Always, or Off.");
        PanelPosition = Config.Bind("Panels", "QoL panel position", "", "Where the QoL panel was dragged to (left, top), or empty for the right side.");
        ShortcutsPosition = Config.Bind("Panels", "Shortcuts box position", "", "Where the Shortcuts box was dragged to (left, top in screen pixels), or empty for beside the part panel.");
        MenuOpens = Config.Bind("Right-click menu", "Opens with", "Ctrl", new ConfigDescription(
            "Held with a right click that doesn't drag, opens the editor's right-click menu: Ctrl, Alt, Shift, or Off for no menu.",
            new AcceptableValueList<string>(RightClickMenu.Holds)));
        MenuHidden = Config.Bind("Right-click menu", "Hidden items", "", "Items left out of the right-click menu, as Section/Item, separated by |");
        ToolSettings.Load(Config);
        MeshTools.RegisterPrecisionSettings();
        ToolSettings.RegisterFields(typeof(HoleQuality), "Holes", "segments", "sizePercent", "cardinalPoints", "holeFill", "holePerFace");
        ToolSettings.RegisterFields(typeof(ShapeTools), "Shape tools", "keepCutter", "rectangleBox", "fill", "clothCell", "clothStiffness", "clothNet", "netHole", "netHoleMm", "landsOn", "scatterCount", "scatterUp");
        WorkshopLibrary.Initialize(Paths.BepInExRootPath);
        CommandPalette.Register(MeshTools.PrecisionCommands);
        MeshAssistSettings.Load(Config);
        Keybinds.Load(Config);
        ModOptions.Register();
        ShareWithModApi();
        AddComponent<DesignEditor>();
        AddComponent<DriveRecorder>();
        var harmony = new Harmony("local.sprocket.qol");
        var features = new[] { typeof(InspectorSection), typeof(MassMarkers), typeof(TurretDriveRepair), typeof(TurretMotorLifecycle), typeof(ShapeTools), typeof(RestoreSection), typeof(UndoStepLifetime), typeof(HoleQuality), typeof(MeshTools), typeof(MeshSelection), typeof(BuildAssist), typeof(WorkshopLibrary), typeof(MergeFaces), typeof(Hotkeys), typeof(TurretCopy), typeof(ExplodedView), typeof(GunLength), typeof(GearSpeeds), typeof(PartPaint), typeof(PartPaint.SavedPaintLoad), typeof(ImageAddresses), typeof(DrawingSheet.NoHover), typeof(DrawingSettings), typeof(RotationSnap), typeof(MirrorMerge), typeof(MirrorSplit), typeof(ObjTransfer), typeof(ModOptions), typeof(NativePanels), typeof(RightClickMenu) };
        features = features.Concat(new[] { typeof(CommandPalette), typeof(PartPatterns) }).ToArray();
        // One hook at a time: a game method a game update removed turns off that hook only (Mod Options > Compatibility).
        foreach (var feature in features) Hooks.Attach(harmony, feature);
        // The engine panel's class moved assembly in Sprocket 0.2.56: found while the game runs (EngineEditor), not named.
        Hooks.AttachPostfix(harmony, nameof(GearSpeeds), EngineEditor.Type, "CombustionEngineComponentEditor", "OnComponentRebuilt", AccessTools.Method(typeof(GearSpeeds), nameof(GearSpeeds.EngineChanged)));
        Hooks.AttachPostfix(harmony, nameof(NativePanels), EngineEditor.Type, "CombustionEngineComponentEditor", "OnGUI", AccessTools.Method(typeof(NativePanels), nameof(NativePanels.Engine)));
        PanelSections.Register();
        if (Hooks.Failures.Any()) Log.LogWarning("Quality of Life: " + Hooks.Summary());
        else Log.LogInfo("Quality of Life: " + Hooks.Summary());
        Log.LogInfo("Quality of Life loaded: Turret to Add-on, Merge add-ons, Cut with add-on, Hole quality, Merge faces, Mesh tools, Hotkeys, Turret copy, Exploded view, Gun length, Speed & acceleration, Max-quality photo, Own paint, Bridge, Circle, Fix mirror, Mirror merge, Mirror split, Rotation snap, OBJ export / import (F10).");
    }

    /// The settings on QoL's page in the Sprocket Mod API's Mod menu, when the API is installed. The keys are in its
    /// keybinding window (Keybinds); what QoL keeps for itself (positions, folds, gun choices) stays off the page.
    void ShareWithModApi()
    {
        var own = new HashSet<string> { "Panels/Folded sections", "Panels/QoL panel position", "Panels/Shortcuts box position", "Drawing sheet/Hidden gun limits" };
        ModApi.ShareConfig(Log, Config, "Quality of Life",
            include: d => d.Section != "Keybinds" && !own.Contains(d.Section + "/" + d.Key),
            range: d => (d.Section + "/" + d.Key) switch
            {
                "Drawing sheet/Wireframe intensity percent" or "Drawing sheet/Grid intensity percent" => (0, 100, 5),
                "Panels/Exploded view spread" => (0, 5, 0.1),
                "Panels/Flashlight brightness" => (0, 200, 5),
                "Panels/Fullbright brightness" => (0, 100, 5),
                "Editor/Rotation snap (degrees)" => (0, 45, 0.5),
                "Selection/Click radius (pixels)" => (4, 30, 1),
                "Build assist/Suggestion radius (pixels)" => (4, 40, 1),
                "Backups/Backups kept" => (0, 500, 10),
                _ => null,
            },
            choices: d => d.Section == "Panels" && d.Key == "QoL panel" ? new[] { ("Automatic", "Automatic"), ("Always", "Always"), ("Off", "Off") } : null);
    }
}
