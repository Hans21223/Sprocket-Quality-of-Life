using Sprocket.PlateMesh;
using Sprocket.Vehicles.PlateStructures.Design;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

/// Smart corners in the mesh panel: the game's own thickening worked out each way for every Auto corner of the chosen
/// faces, and the mode that meets the neighbouring plates best kept (SmartCorners has the maths).
public static partial class MeshTools
{
    internal static void SmartCornersTool(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        Run(e, "Smart corners", mesh =>
        {
            var v = new View(mesh);
            var faces = WithTwins(v, v.SelectedFaces.Count > 0 ? new HashSet<int>(v.SelectedFaces) : Enumerable.Range(0, v.Faces.Count).ToHashSet(), mirror);
            int auto = 0, changed = 0;
            var chosen = new Dictionary<string, int>();
            foreach (int f in faces)
            {
                var face = v.Faces[f];
                var loop = face.firstLoop;
                for (int k = 0; k < face.vertexCount && loop != null; k++, loop = loop.next)
                {
                    if (loop.thickenMode != ThickenMode.Auto) continue; // a mode set on purpose stays
                    auto++;
                    if (Smarten(loop) is string mode) { changed++; chosen[mode] = chosen.GetValueOrDefault(mode) + 1; }
                }
            }
            if (auto == 0) return (false, "no corner of these faces is on Auto (corners set on purpose are left as they are)");
            if (changed == 0) return (false, $"all {auto} Auto corners already meet their plates as well as the game's modes can");
            mesh.MarkDirty(MeshDirtyFlags.All);
            return (true, $"set {changed} of {auto} Auto corners ({string.Join(", ", chosen.Select(c => $"{c.Value} {c.Key}"))})");
        });
    }

    static Num ToNum(UnityEngine.Vector3 p) => new(p.x, p.y, p.z);

    /// Tries every mode on one Auto corner and keeps the best; what it chose, or null if Auto was best (left as it was).
    static string? Smarten(Loop loop)
    {
        var vertex = loop.vertex;
        var corner = ToNum(vertex.position);
        var normal = vertex.normal;
        // A plate's inner side at its corner, as the game thickens it straight in from the face.
        SmartCorners.Plate? Plate(Loop? at)
        {
            if (at == null) return null;
            EditMeshBuilding.ThickenAlongFaceNormal(at);
            var inward = ToNum(at.thickenedPosition) - ToNum(at.vertex.position);
            EditMeshBuilding.ComputeLoopThickenPosition(at, at.vertex.normal); // its own mode's position back
            return inward.Length() > 1e-7f ? new SmartCorners.Plate(Num.Normalize(inward), inward.Length()) : null;
        }
        // The neighbour across a side, at this corner (it runs the shared side the other way).
        Loop? Across(Loop side) => side.radialNext is { } r && r.Pointer != side.Pointer ? (r.vertex.Pointer == vertex.Pointer ? r : r.next) : null;
        if (Plate(loop) is not { } self) return null;
        var ideal = SmartCorners.Ideal(corner, self, Plate(Across(loop)), ToNum(loop.next.vertex.position) - corner,
                                       Plate(Across(loop.prev)), corner - ToNum(loop.prev.vertex.position));
        if (ideal == null) return null;

        // Where each of the game's modes puts the inner corner: Auto (as it is), straight in, along the point's normal, the
        // game's own edge choice, and along each other edge from the point.
        var options = new List<(string Name, ThickenMode Mode, Edge? Edge, Num At)>();
        void Try(string name, ThickenMode mode, Edge? edge = null)
        {
            loop.thickenMode = mode;
            loop.thickenEdge = edge;
            EditMeshBuilding.ComputeLoopThickenPosition(loop, normal);
            options.Add((name, mode, edge, ToNum(loop.thickenedPosition)));
        }
        Try("Auto", loop.thickenMode, loop.thickenEdge); // as it is: kept unless another is clearly better
        Try("straight in", ThickenMode.AlongFaceNormal);
        Try("along the point", ThickenMode.AlongVertexNormal);
        Try("along an edge", ThickenMode.AlongEdgeAuto);
        var first = vertex.firstEdge;
        var edge = first;
        for (int i = 0; edge != null && i < 64; i++)
        {
            if (edge.Pointer != loop.edge.Pointer && edge.Pointer != loop.prev.edge.Pointer) Try("along a chosen edge", ThickenMode.AlongEdgeManual, edge);
            edge = edge.GetNextDiskEdge(vertex);
            if (edge == null || edge.Pointer == first.Pointer) break;
        }
        int best = SmartCorners.Best(corner, self.Thickness, ideal.Value, options.Select(o => o.At).ToList(), 0);
        var pick = options[best];
        loop.thickenMode = pick.Mode;
        loop.thickenEdge = pick.Edge;
        EditMeshBuilding.ComputeLoopThickenPosition(loop, normal);
        return best == 0 ? null : pick.Name;
    }
}
