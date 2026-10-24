using HarmonyLib;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;

namespace SprocketQoL;

/// Hover outlines: while the mouse is over a mesh tool's button, what the tool would make or move is outlined on the
/// part (the tool run on a copy of the mesh), instead of a window to confirm. Create Hole draws its rings itself.
public static partial class MeshTools
{
    static Sprocket.Tooltiped? hoveredButton;
    static bool dryRun; // inside a hover outline: Run keeps what the tool would do instead of doing it
    static MeshEditOp? caught; // a game tool's edit, caught before it ran
    static (string Key, Vector3[] Points, int[][] Faces, bool Gone)? hoverWire; // Gone: faces it would take away (Delete)

    /// Outlining: QoL's mirror hooks also work on the mesh copy then.
    internal static bool Outlining => dryRun;
    static float hoverAt;

    /// The title of the game or QoL button under the mouse (its tooltip's), or null.
    internal static string? HoveredButton => hoveredButton != null && hoveredButton.isActiveAndEnabled ? hoveredButton.header : null;

    /// Counts the mouse coming onto a button: outlines kept for a hover are worked out again on the next (settings or
    /// points may have changed while the mouse was elsewhere).
    internal static int HoverCount { get; private set; }

    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.Tooltiped), nameof(Sprocket.Tooltiped.OnPointerEnter))]
    static void OverButton(Sprocket.Tooltiped __instance) { hoveredButton = __instance; HoverCount++; }

    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.Tooltiped), nameof(Sprocket.Tooltiped.OnPointerExit))]
    static void OffButton(Sprocket.Tooltiped __instance) { if (hoveredButton?.Pointer == __instance.Pointer) hoveredButton = null; }

    // The tools' "select an edge first" and the like stay quiet while only outlining: QoL's and the game's own.
    [HarmonyPrefix, HarmonyPatch(typeof(PlateStructureEditOperations), nameof(PlateStructureEditOperations.NotifyError))]
    static bool QuietWhileOutlining() => !dryRun;

    [HarmonyPrefix, HarmonyPatch(typeof(Sprocket.Info), nameof(Sprocket.Info.SendError))]
    static bool QuietGameWhileOutlining() => !dryRun;

    // While outlining, a game tool hands its edit over instead of running it.
    [HarmonyPrefix, HarmonyPatch(typeof(PlateStructureEditOperations), "Execute", new[] { typeof(MeshEditOp), typeof(StructureEditOperationOptions), typeof(int) })]
    static bool CatchEdit(MeshEditOp __0, ref StructureEdit? __result)
    {
        if (!dryRun) return true;
        caught = __0;
        __result = null;
        return false;
    }

    // The game's toolbar buttons outlined: those that only make one mesh edit and don't use it after (Slope does).
    static readonly Dictionary<string, Action<PlateStructureEditOperations, int>> gameTools = new()
    {
        ["Fill"] = (o, g) => o.Fill(g), ["Merge"] = (o, g) => o.Merge(g), ["Split"] = (o, g) => o.Split(g), ["Delete"] = (o, g) => o.Delete(g),
    };

    // Button titles (their tooltips') and the tools they run.
    static readonly Dictionary<string, string> outlined = new()
    {
        ["Flatten"] = "Flatten", ["Loop cut"] = "Loop cut", ["Inset"] = "Inset", ["Bevel"] = "Bevel", ["Smooth Edge"] = "Smooth Edge",
        ["Fillet"] = "Fillet", ["Bridge"] = "Bridge", ["Circle"] = "Circle", ["Split faces"] = "Split Face", ["Slide"] = "Slide selection",
        ["Exact distance"] = "Set selection length", ["Parallel alignment"] = "Align selection", ["Weld duplicates"] = "Weld selected duplicates",
        ["Fix mirror"] = "Fix mirror",
    };

    internal static void DrawHoverTool()
    {
        if (HoveredButton is not { } title || !outlined.ContainsKey(title) && !gameTools.ContainsKey(title) || Event.current.type != EventType.Repaint
            || Hotkeys.Current is not { } e || e.meshEditor?.Mesh?.EditMesh is not { } mesh || !MeshSelection.TryMeshFrame(e, out var frame)
            || Camera.main is not { } camera) return;
        string key = $"{HoverCount}|{title}|{mesh.Pointer}|{mesh.vertices.Count}|{mesh.faces.Count}|{mesh.selectedVertexCount}|{mesh.selectedEdgeCount}|{mesh.selectedFaceCount}|{e.meshEditor.Symmetry}"
            + $"|{SelectionFingerprint(mesh)}|{ToolSettings.Fingerprint()}"; // a setting, or the selection or where it is, changed
        if (hoverWire?.Key != key && Time.unscaledTime >= hoverAt)
        {
            hoverAt = Time.unscaledTime + 0.25f;
            hoverWire = (key, Array.Empty<Vector3>(), Array.Empty<int[]>(), false);
            var split = (lastSplitMesh, lastSplitFaces); // Split faces remembers its last split for Select between splits
            dryRun = true;
            caught = null;
            try
            {
                if (outlined.TryGetValue(title, out var tool)) { if (tool == "Fix mirror") FixMirror(e); else InvokeTool(e, tool); }
                else
                {
                    gameTools[title](e.operations, e.operations.GetNewGroupID());
                    if (caught?.TryCast<MeshTopologyEditOp>()?.meshOp is { } op) Outline(e, copy => { copy.ExecuteOperation(op); return (true, ""); });
                }
            }
            finally { dryRun = false; caught = null; (lastSplitMesh, lastSplitFaces) = split; }
        }
        if (hoverWire is { Points.Length: > 0 } wire)
            GeometryPreview.DrawWire(camera, wire.Points, wire.Faces, frame, wire.Gone ? new Color(1, .3f, .25f) : new Color(1, .85f, .2f));
    }

    /// Run while outlining: the tool on a copy of the mesh. The faces it would make or move are kept to draw, or if it
    /// makes none (Delete), the faces it would take away.
    static void Outline(PlateStructureEditor editor, Func<EditMesh, (bool, string)> apply)
    {
        var live = editor.meshEditor?.Mesh?.EditMesh;
        if (live == null || CloneProblem(live) != null || hoverWire is not { } wire) return;
        var copy = new EditMesh(live);
        if (!apply(copy).Item1) return;
        // A face by its corners' positions (to 0.01 mm), whichever corner it starts at: a number, not text (formatting
        // every corner of a big shape as text took a noticeable moment on each hover).
        static long Corners(Face f)
        {
            long key = 0;
            foreach (var p in HoleQuality.Corners(f, v => v.position))
            {
                long h = (long)Math.Round(p.x * 1e5) * 73856093 ^ (long)Math.Round(p.y * 1e5) * 19349663 ^ (long)Math.Round(p.z * 1e5) * 83492791;
                key += (h ^ (h >> 29)) * 0x2545F4914F6CDD1D; // summed, so the corners' order doesn't matter
            }
            return key;
        }
        static HashSet<long> All(EditMesh m) { var all = new HashSet<long>(); for (int i = 0; i < m.faces.Count; i++) all.Add(Corners(m.faces[i])); return all; }
        (Vector3[], int[][]) Except(EditMesh from, HashSet<long> others)
        {
            var points = new List<Vector3>();
            var faces = new List<int[]>();
            for (int i = 0; i < from.faces.Count; i++)
            {
                if (others.Contains(Corners(from.faces[i]))) continue;
                var corners = HoleQuality.Corners(from.faces[i], v => v.position);
                faces.Add(Enumerable.Range(points.Count, corners.Length).ToArray());
                points.AddRange(corners);
            }
            return (points.ToArray(), faces.ToArray());
        }
        var (made, madeFaces) = Except(copy, All(live));
        if (made.Length > 0) { hoverWire = (wire.Key, made, madeFaces, false); return; }
        var (gone, goneFaces) = Except(live, All(copy));
        hoverWire = (wire.Key, gone, goneFaces, true);
    }
}
