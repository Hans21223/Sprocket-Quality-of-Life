using Sprocket.PlateMesh;
using Sprocket.VehicleDesigner;
using Sprocket.Vehicles.PlateStructures.Design;

namespace SprocketQoL;

/// QoL's own edits keep Horizontal corners off faces too small for a normal (DesignEditor.Guarded, MeshTools.Apply). A face
/// can still get that small afterwards: points moved or scaled, the game's own Create Hole, Separate. So after each change
/// to the shape being edited, its corners are checked the same way; one left there crashes Play (240, N-120MBT).
internal static class ZeroNormalGuard
{
    static IntPtr lastMesh;
    static int lastUndo = int.MinValue;

    internal static void Tick(VehicleDesignerCore core)
    {
        if (core.Editor == null || core.Editor.OperationInProgress) return;
        if (PanelSections.Active<FreeformPlateStructureEditor>()?.meshEditor?.Mesh?.EditMesh is not { } mesh) { lastMesh = IntPtr.Zero; return; }
        int undo = core.Editor.ops?.UndoCount ?? -1;
        if (mesh.Pointer == lastMesh && undo == lastUndo) return; // nothing changed since the last look
        lastMesh = mesh.Pointer; lastUndo = undo;
        int changed = 0;
        var faces = mesh.faces;
        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            if (face == null || face.vertexCount < 3 || face.HasFlag(ElementFlags.Delete)) continue;
            var l = face.firstLoop;
            bool horizontal = false;
            for (int k = 0; k < face.vertexCount && !horizontal; k++, l = l.next) horizontal = l.thickenMode == ThickenMode.Horizontal;
            if (!horizontal || !MeshTools.TooSmallForNormal(face)) continue;
            l = face.firstLoop;
            for (int k = 0; k < face.vertexCount; k++, l = l.next)
                if (l.thickenMode == ThickenMode.Horizontal) { l.thickenMode = ThickenMode.AlongFaceNormal; changed++; }
        }
        if (changed == 0) return;
        mesh.MarkDirty(MeshDirtyFlags.All);
        Plugin.ModLog.LogInfo($"QOL_GUARD {changed} Horizontal corner(s) on faces too small for a normal set to straight in after an editor change (they would crash Play)");
    }
}
