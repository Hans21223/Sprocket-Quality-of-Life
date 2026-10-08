using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.MeshEditing;
using Sprocket.PartImporting;
using Sprocket.PlateMesh;
using Sprocket.PlateMesh.Operations;
using Sprocket.PlateMesh.Rivets;
using Sprocket.Transformations;
using Sprocket.UI;
using Sprocket.Vehicles.PartImporting;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

/// Blender-style mesh tools for hand-made structures (MeshPlans does the maths): Flatten (P), Loop cut (T), Inset (I),
/// Bevel (V), Smooth Edge, Fillet, Select linked flat faces (U) and Proportional editing (O); plus a 0.5 mm grid and an orthographic view
/// (Numpad 5). Each tool runs as one of the game's own mesh edits, so Ctrl+Z undoes it, and follows the editor's Mirror.
[HarmonyPatch]
public static class MeshTools
{
    // ---------- running a tool as one of the game's mesh edits ----------

    // The game's Delete operation, given nothing to delete, carries a tool so it gets the game's undo and mesh rebuild.
    // Only these instances run a tool; every other Delete runs as normal.
    static readonly Dictionary<IntPtr, (DeleteOp Op, string Name, Func<EditMesh, (bool Done, string Message)> Apply, MeshTopologyEditOp Topology, EditMesh Original)> ours = new();
    static readonly Dictionary<IntPtr, IntPtr> carriedTools = new();
    static PlateStructureEditOperations? notify;

    [HarmonyPrefix, HarmonyPatch(typeof(DeleteOp), "ExecuteInternal")]
    static bool Intercept(DeleteOp __instance, EditMesh mesh)
    {
        if (!ours.TryGetValue(__instance.Pointer, out var tool)) return true;
        try
        {
            var (done, message) = tool.Apply(mesh);
            if (CloneProblem(mesh) is string problem) throw new InvalidOperationException(problem);
            Plugin.ModLog.LogInfo($"{tool.Name}: {message}");
            if (!done) notify?.NotifyError($"{tool.Name}: {message}");
        }
        catch (Exception ex)
        {
            // IL2CPP's Harmony trampoline logs and swallows managed exceptions, so
            // throwing does not abort this transaction. Reattach the untouched original
            // explicitly, and make redo point to that valid mesh instead of this candidate.
            tool.Topology.final = tool.Original;
            tool.Topology.Revert(tool.Topology.context);
            tool.Original.MarkDirty(MeshDirtyFlags.All);
            Plugin.ModLog.LogError($"{tool.Name}: edit cancelled; original mesh restored. {ex}");
            notify?.NotifyError(tool.Name + ": edit cancelled; the original shape was restored");
        }
        return false;
    }

    internal static void Run(PlateStructureEditor editor, string name, Func<EditMesh, (bool, string)> apply) => Ui.Guard(name, () =>
    {
        var designer = DesignEditor.Instance;
        if (DrawingSheet.Capturing || PhotoShot.Capturing || designer?.IsBusy == true || designer?.Core?.Editor?.OperationInProgress == true)
        {
            editor.operations.NotifyError(name + ": finish the current edit or capture first");
            return;
        }
        var currentMesh = editor.meshEditor?.Mesh?.EditMesh;
        if (currentMesh == null) return;
        RepairThickening(currentMesh);
        if (CloneProblem(currentMesh) is string problem)
        {
            editor.operations.NotifyError(name + ": " + problem);
            Plugin.ModLog.LogError(name + ": " + problem);
            return;
        }
        var op = new DeleteOp(DeleteType.None) { Name = name };
        var topology = editor.meshEditor!.CreateTopoOp(op);
        ours[op.Pointer] = (op, name, apply, topology, currentMesh);
        var ops = notify = editor.operations;
        bool retained = false;
        try
        {
            var carried = ops.Execute(topology, StructureEditOperationOptions.None, ops.GetNewGroupID());
            if (carried?.operation is { } historyOp)
            {
                carriedTools[historyOp.Pointer] = op.Pointer;
                retained = true;
            }
            else ours.Remove(op.Pointer);
            editor.meshEditor.SelectFlush();
        }
        catch
        {
            topology.final = currentMesh;
            topology.Revert(topology.context);
            currentMesh.MarkDirty(MeshDirtyFlags.All);
            if (!retained) ours.Remove(op.Pointer);
            throw;
        }
        finally { notify = null; }
    });

    // Keep tool callbacks for redo only as long as their native undo operation lives.
    // ReleaseInternal is invoked when the game discards/clears an operation's history.
    [HarmonyPrefix, HarmonyPatch(typeof(Operations.Operation), nameof(Operations.Operation.ReleaseInternal))]
    static void ReleaseTool(Operations.Operation __instance) => Ui.Guard("Mesh tool history", () =>
    {
        if (carriedTools.Remove(__instance.Pointer, out var tool)) ours.Remove(tool);
    });

    /// The mesh as indices: every face's corners (in its own turning), point positions, and what's selected.
    internal sealed class View
    {
        public readonly List<Vertex> Verts = new();
        public readonly List<Num> Pos = new();
        public readonly List<Face> Faces = new();
        public readonly List<int[]> Corners = new();
        public readonly HashSet<int> SelectedFaces = new(), SelectedPoints = new();
        public readonly List<(int A, int B)> SelectedEdges = new();
        public readonly HashSet<(int, int)> Loose = new();
        readonly Dictionary<IntPtr, int> index = new();

        public int Id(Vertex v)
        {
            if (!index.TryGetValue(v.Pointer, out int i)) { index[v.Pointer] = i = Verts.Count; Verts.Add(v); Pos.Add(HoleQuality.ToNum(v.position)); }
            return i;
        }

        public View(EditMesh mesh)
        {
            var faces = mesh.faces;
            for (int i = 0; i < faces.Count; i++)
            {
                Faces.Add(faces[i]);
                Corners.Add(HoleQuality.Corners(faces[i], Id));
                if (faces[i].HasFlag(ElementFlags.Selected)) SelectedFaces.Add(i);
            }
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Count; i++) if (vertices[i].HasFlag(ElementFlags.Selected)) SelectedPoints.Add(Id(vertices[i]));
            var edges = mesh.edges;
            for (int i = 0; i < edges.Count; i++)
            {
                int a = Id(edges[i].v0), b = Id(edges[i].v1);
                if (edges[i].HasFlag(ElementFlags.Selected)) SelectedEdges.Add((a, b));
                if (edges[i].loop == null) Loose.Add(FaceMerge.Key(a, b));
            }
        }

        /// The corner of face `f` at point `v`, or null.
        public Loop? CornerIn(int f, int v)
        {
            int k = Array.IndexOf(Corners[f], v);
            if (k < 0) return null;
            var l = Faces[f].firstLoop;
            for (int i = 0; i < k; i++) l = l.next;
            return l;
        }
    }

    internal static float Tolerance => MeshTransformation.MirrorMaxDistance > 0 ? MeshTransformation.MirrorMaxDistance : 0.0003f;

    /// With Mirror on, the mirrored twins of these edges (both ends must have a twin, and a face must use the edge).
    static List<(int, int)> WithTwins(View v, List<(int A, int B)> edges, bool mirror)
    {
        if (!mirror) return edges;
        var twins = MeshPlans.Twins(v.Pos, edges.SelectMany(e => new[] { e.A, e.B }).Distinct(), Tolerance);
        var used = v.Corners.SelectMany(c => c.Select((p, k) => FaceMerge.Key(p, c[(k + 1) % c.Length]))).ToHashSet();
        return edges.Concat(edges.Where(e => twins.ContainsKey(e.A) && twins.ContainsKey(e.B) && used.Contains(FaceMerge.Key(twins[e.A], twins[e.B])))
                                 .Select(e => (twins[e.A], twins[e.B]))).Distinct().ToList();
    }

    internal static HashSet<int> WithTwins(View v, HashSet<int> faces, bool mirror)
    {
        if (mirror) faces.UnionWith(FaceMerge.Mirrored(v.Pos, v.Corners, faces.ToList(), Tolerance));
        return faces;
    }

    /// Takes out the plan's faces and puts in its new ones, with the game's own mesh calls. Old corners keep their face's
    /// thickness and thickening; new points blend from the corners they come from. Edges and points nothing uses go.
    static (bool, string) Apply(EditMesh mesh, View view, MeshPlans.Rebuild plan, string did, bool gaps = true, bool preserveBevelEdges = false, ISet<(int,int)>? roundedEdges = null, Action<List<(Face Face,int Source)>>? afterApply = null)
    {
        if (plan.Why != null) return (false, plan.Why);
        if (MeshPlans.Check(view.Pos, view.Corners, plan, gaps) is string broken) return (false, "not done: " + broken);
        var edgeSources = preserveBevelEdges ? BevelEdges.Sources(view.Pos, view.Corners, plan, roundedEdges) : null;
        // Keep the authored state before creating any topology. The prototype's temporary
        // tessellation/check flags must not spread into the new chamfer or its neighbouring sides.
        const ElementFlags edgeSettings = ElementFlags.Selected | ElementFlags.Sharp | ElementFlags.AlternatePlateConnection;
        var oldEdges = new Dictionary<(int, int), (Edge Edge, ElementFlags Settings)>();
        if (preserveBevelEdges)
            foreach (var edge in mesh.edges)
                oldEdges[FaceMerge.Key(view.Id(edge.v0), view.Id(edge.v1))] = (edge, edge.flags & edgeSettings);
        int old = view.Pos.Count;
        var verts = new List<Vertex>(view.Verts);
        var ids = new Dictionary<IntPtr, int>();
        for (int i = 0; i < verts.Count; i++) ids[verts[i].Pointer] = i;
        foreach (var p in plan.Points)
        {
            // CreateVertex copies its prototype's position, so place the new point after creating it.
            var at = new Vector3(p.P.X, p.P.Y, p.P.Z);
            var v = mesh.CreateVertex(verts[p.Blend[0].V], at);
            v.position = at;
            ids[v.Pointer] = verts.Count;
            verts.Add(v);
        }
        var removed = plan.Remove.ToHashSet();
        var anyCorner = new Dictionary<int, Loop>();
        foreach (int f in plan.Remove.Concat(Enumerable.Range(0, view.Faces.Count)))
            foreach (int v in view.Corners[f]) if (!anyCorner.ContainsKey(v)) anyCorner[v] = view.CornerIn(f, v)!;
        Loop Corner(int f, int v) => view.CornerIn(f, v) ?? (anyCorner.TryGetValue(v, out var any) ? any : view.Faces[f].firstLoop);
        float Thickness(int f, int v)
        {
            if (v < old) return Corner(f, v).thickness;
            var blend = plan.Points[v - old].Blend;
            float total = blend.Sum(b => b.W);
            return total > 0 ? blend.Sum(b => b.W * Thickness(f, b.V)) / total : Corner(f, blend.Length > 0 ? blend[0].V : (old > 0 ? 0 : 0)).thickness;
        }
        Loop From(int f, int v) => Corner(f, v < old ? v : plan.Points[v - old].Blend[0].V);

        // Edges and points still in use afterwards: everything the kept and new faces touch, and loose edges.
        var keptEdges = new HashSet<(int, int)>(view.Loose);
        var keptPoints = view.Loose.SelectMany(e => new[] { e.Item1, e.Item2 }).ToHashSet();
        foreach (var c in Enumerable.Range(0, view.Faces.Count).Where(f => !removed.Contains(f)).Select(f => view.Corners[f]).Concat(plan.Add.Select(a => a.Corners)))
            for (int k = 0; k < c.Length; k++) { keptEdges.Add(FaceMerge.Key(c[k], c[(k + 1) % c.Length])); keptPoints.Add(c[k]); }

        var rivets = new RivetKeeper(mesh, removed.Select(f => view.Faces[f]));
        var made = new List<(Face Face, int Source)>();
        foreach (var nf in plan.Add)
        {
            var source = view.Faces[nf.Source];
            var vs = nf.Corners.Select(i => verts[i]).ToArray();
            var es = new Edge[vs.Length];
            for (int k = 0; k < vs.Length; k++)
            {
                var a = vs[k];
                var b = vs[(k + 1) % vs.Length];
                if (Edge.GetConnectingEdge(a, b) is { } existing) { es[k] = existing; continue; }
                if (edgeSources != null)
                {
                    var key = FaceMerge.Key(nf.Corners[k], nf.Corners[(k + 1) % vs.Length]);
                    bool inherited = edgeSources.TryGetValue(key, out var from) && oldEdges.ContainsKey(from);
                    var prototype = inherited ? oldEdges[from] : default;
                    es[k] = mesh.CreateEdge(a, b, inherited ? prototype.Edge : null);
                    es[k].flags = inherited ? prototype.Settings : ElementFlags.None;
                }
                else
                {
                    es[k] = mesh.CreateEdge(a, b, source.firstLoop.edge);
                    es[k].DisableFlag(ElementFlags.Sharp);
                }
            }
            made.Add((mesh.CreateFace(new Il2CppReferenceArray<Vertex>(vs), new Il2CppReferenceArray<Edge>(es), source,
                new Il2CppStructArray<ushort>(nf.Corners.Select(i => (ushort)Math.Round(Thickness(nf.Source, i))).ToArray())), nf.Source));
        }
        foreach (int f in removed)
        {
            var l = view.Faces[f].firstLoop;
            for (int k = 0; k < view.Faces[f].vertexCount; k++, l = l.next)
                if (!keptEdges.Contains(FaceMerge.Key(ids[l.vertex.Pointer], ids[l.next.vertex.Pointer]))) l.edge.EnableFlag(ElementFlags.Delete);
            view.Faces[f].EnableFlag(ElementFlags.Delete);
            foreach (int v in view.Corners[f]) if (!keptPoints.Contains(v)) verts[v].EnableFlag(ElementFlags.Delete);
        }
        foreach (var (face, source) in made)
        {
            var l = face.firstLoop;
            for (int k = 0; k < face.vertexCount; k++, l = l.next)
            {
                var from = From(source, ids[l.vertex.Pointer]);
                CopyThickening(l, from);
            }
        }
        var (kept, lost) = rivets.Place(made.Select(m => m.Face), reach: 0.05f);
        FinishDelete(mesh);
        afterApply?.Invoke(made);
        var problems = made.Select(m => HoleQuality.Problem(m.Face)).Where(p => p != null).Distinct().ToList();
        return (true, $"{did}: {removed.Count} faces became {made.Count}, {plan.Points.Count} new points" +
                      (rivets.Count == 0 ? "" : $", rivets {kept} kept" + (lost > 0 ? $" {lost} lost" : "")) +
                      (problems.Count == 0 ? ", mesh checks OK" : ", MESH CHECK FAILED: " + string.Join("; ", problems)));
    }

    /// Rivets on faces a tool replaces: the game drops a face's rivets with it, so where each sits is noted before the
    /// faces change, and once the new faces are made each goes onto the one it lies on (or the nearest, within `reach`:
    /// a bevel cuts a corner away). Its line links, type and flags stay. Placed before the old faces are deleted.
    internal sealed class RivetKeeper
    {
        readonly RivetMap? map;
        readonly RivetMeshLayer? layer;
        readonly List<(RivetNode Node, Num At, RivetFlags Flags, byte Profile)> moving = new();

        public RivetKeeper(EditMesh mesh, IEnumerable<Face> leaving)
        {
            var layers = mesh.layers;
            for (int i = 0; i < layers.Count && layer == null; i++) layer = layers[i]?.TryCast<RivetMeshLayer>();
            map = layer?.Map;
            if (map == null) return;
            var gone = leaving.Select(f => f.Pointer).ToHashSet();
            var nodes = map.Nodes;
            int count = nodes.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<RivetNode>>().Count;
            for (int i = 0; i < count; i++)
                if (nodes[i] is { face: { } f } n && gone.Contains(f.Pointer))
                    moving.Add((n, HoleQuality.ToNum(RivetMap.GetCartesianCoordinates(f, n.coord, n.faceOffset)), n.flags, n.profile));
        }

        public int Count => moving.Count;

        /// Puts the noted rivets on `made`; how many found a face and how many didn't (those go with the old faces).
        public (int Moved, int Lost) Place(IEnumerable<Face> made, float reach)
        {
            if (map == null || moving.Count == 0) return (0, 0);
            var faces = made.Where(f => f != null).Select(f => (Face: f, Corners: HoleQuality.Corners(f))).ToList();
            var still = new HashSet<IntPtr>();
            var nodes = map.Nodes;
            int count = nodes.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<RivetNode>>().Count;
            for (int i = 0; i < count; i++) if (nodes[i] != null) still.Add(nodes[i].Pointer);
            int moved = 0, lost = 0;
            foreach (var (node, at, flags, profile) in moving)
            {
                var best = faces.Select(f => (f.Face, Near: MeshPlans.Closest(f.Corners, at))).OrderBy(x => x.Near.Distance).FirstOrDefault();
                if (best.Face == null || best.Near.Distance > reach) { lost++; continue; }
                var p = new Vector3(best.Near.Point.X, best.Near.Point.Y, best.Near.Point.Z);
                RivetMap.GetBarycentricCoordinatesOnFace(best.Face, p, out var coord, out sbyte offset);
                if (still.Contains(node.Pointer)) map.SetPosition(node, best.Face, coord, offset);
                else map.Create(best.Face, coord, offset, flags, profile); // the game had dropped it already: a new one in its place
                moved++;
            }
            layer?.MarkModified();
            return (moved, lost);
        }
    }

    /// Copy authored corner settings; a new point cannot use its source point's edge.
    internal static void CopyThickening(Loop destination, Loop source)
    {
        destination.thickenMode = source.thickenMode;
        var edge = source.thickenEdge;
        destination.thickenEdge = edge != null && !edge.HasFlag(ElementFlags.Delete) &&
            (edge.v0.Pointer == destination.vertex.Pointer || edge.v1.Pointer == destination.vertex.Pointer) ? edge : null;
        if (destination.thickenEdge == null && destination.thickenMode == ThickenMode.AlongEdgeManual)
            destination.thickenMode = ThickenMode.AlongEdgeAuto;
    }

    /// DeleteMarked only compacts the lists. Native deletion also unlinks disk/radial
    /// cycles, so auto thickening cannot rediscover edges that no longer belong to the mesh.
    internal static int FinishDelete(EditMesh mesh)
    {
        var faces = Enumerable.Range(0, mesh.faces.Count).Select(i => mesh.faces[i]).Where(f => f.HasFlag(ElementFlags.Delete)).ToList();
        var edges = Enumerable.Range(0, mesh.edges.Count).Select(i => mesh.edges[i]).Where(e => e.HasFlag(ElementFlags.Delete)).ToList();
        var vertices = Enumerable.Range(0, mesh.vertices.Count).Select(i => mesh.vertices[i]).Where(v => v.HasFlag(ElementFlags.Delete)).ToList();
        var deletedEdges = edges.Select(e => e.Pointer).ToHashSet();
        var deletedVertices = vertices.Select(v => v.Pointer).ToHashSet();
        foreach (var edge in mesh.edges)
            if (!deletedEdges.Contains(edge.Pointer) && (deletedVertices.Contains(edge.v0.Pointer) || deletedVertices.Contains(edge.v1.Pointer)))
                throw new InvalidOperationException("a removed point is still used by a surviving edge");
        // The native helpers return early for already marked elements. Clear only the
        // planned marks first, then let the helpers perform and mark their own deletion.
        foreach (var face in faces) face.DisableFlag(ElementFlags.Delete);
        foreach (var edge in edges) edge.DisableFlag(ElementFlags.Delete);
        foreach (var vertex in vertices) vertex.DisableFlag(ElementFlags.Delete);
        foreach (var face in faces) Delete.DeleteFace(mesh, face);
        foreach (var edge in edges) Delete.DeleteEdge(mesh, edge);
        foreach (var vertex in vertices) Delete.DeleteVertex(mesh, vertex);
        int repointed = RepairThickening(mesh);
        mesh.DeleteMarked();
        repointed += RepairThickening(mesh);
        mesh.MarkDirty(MeshDirtyFlags.All);
        if (CloneProblem(mesh) is string problem) throw new InvalidOperationException(problem);
        return repointed;
    }

    /// Validate pointer membership rather than just an edge's recycled numeric index.
    internal static int RepairThickening(EditMesh mesh)
    {
        var edges = Enumerable.Range(0, mesh.edges.Count).Select(i => mesh.edges[i]).Where(e => !e.HasFlag(ElementFlags.Delete)).Select(e => e.Pointer).ToHashSet();
        int repointed = 0;
        foreach (var face in mesh.faces)
        {
            if (face.HasFlag(ElementFlags.Delete)) continue;
            var l = face.firstLoop;
            for (int k = 0; k < face.vertexCount && l != null; k++, l = l.next)
                if (l.thickenEdge is { } edge && (!edges.Contains(edge.Pointer) || l.vertex == null ||
                    edge.v0 == null || edge.v1 == null || !edge.ContainsVertex(l.vertex)))
                {
                    l.thickenEdge = null;
                    if (l.thickenMode == ThickenMode.AlongEdgeManual) l.thickenMode = ThickenMode.AlongEdgeAuto;
                    repointed++;
                }
        }
        if (repointed > 0) mesh.MarkDirty(MeshDirtyFlags.All);
        return repointed;
    }

    /// Native Clone assumes triangles/quads and every referenced element is in these
    /// lists. Face.Validate alone cannot detect a deleted edge lingering in a disk cycle.
    internal static string? CloneProblem(EditMesh mesh)
    {
        var vertices = Enumerable.Range(0, mesh.vertices.Count).Select(i => mesh.vertices[i].Pointer).ToHashSet();
        var edges = Enumerable.Range(0, mesh.edges.Count).Select(i => mesh.edges[i].Pointer).ToHashSet();
        var faces = Enumerable.Range(0, mesh.faces.Count).Select(i => mesh.faces[i].Pointer).ToHashSet();
        var loops = Enumerable.Range(0, mesh.loops.Count).Select(i => mesh.loops[i].Pointer).ToHashSet();
        if (vertices.Count != mesh.vertices.Count || edges.Count != mesh.edges.Count || faces.Count != mesh.faces.Count || loops.Count != mesh.loops.Count)
            return "the mesh contains duplicate element references";
        if (vertices.Count > ushort.MaxValue || edges.Count > ushort.MaxValue || faces.Count > ushort.MaxValue || loops.Count > ushort.MaxValue)
            return "the mesh exceeds the game's element limit";
        foreach (var edge in mesh.edges)
        {
            if (edge.v0 == null || edge.v1 == null || !vertices.Contains(edge.v0.Pointer) || !vertices.Contains(edge.v1.Pointer))
                return "an edge references a point no longer in the mesh";
            foreach (var vertex in new[] { edge.v0, edge.v1 })
            {
                var next = edge.GetNextDiskEdge(vertex);
                var prev = edge.GetPreviousDiskEdge(vertex);
                if (next == null || prev == null || !edges.Contains(next.Pointer) || !edges.Contains(prev.Pointer) ||
                    !next.ContainsVertex(vertex) || !prev.ContainsVertex(vertex) ||
                    next.GetPreviousDiskEdge(vertex)?.Pointer != edge.Pointer || prev.GetNextDiskEdge(vertex)?.Pointer != edge.Pointer)
                    return "an edge is not correctly linked to its points";
            }
            if (edge.loop != null && (!loops.Contains(edge.loop.Pointer) || edge.loop.edge?.Pointer != edge.Pointer))
                return "an edge references a corner no longer in the mesh";
        }
        foreach (var vertex in mesh.vertices)
            if (vertex.firstEdge != null && (!edges.Contains(vertex.firstEdge.Pointer) || !vertex.firstEdge.ContainsVertex(vertex)))
                return "a point references an edge no longer in the mesh";
        var usedLoops = new HashSet<IntPtr>();
        foreach (var face in mesh.faces)
        {
            if (face.vertexCount is < 3 or > 4) return "the game requires every face to have three or four corners";
            var first = face.firstLoop;
            var loop = first;
            for (int k = 0; k < face.vertexCount; k++, loop = loop!.next)
            {
                if (loop == null || !loops.Contains(loop.Pointer) || !usedLoops.Add(loop.Pointer) || loop.face?.Pointer != face.Pointer ||
                    loop.vertex == null || !vertices.Contains(loop.vertex.Pointer) || loop.edge == null || !edges.Contains(loop.edge.Pointer))
                    return "a face references a missing or shared corner, point or edge";
                if (loop.next == null || loop.prev == null || loop.next.prev?.Pointer != loop.Pointer || loop.prev.next?.Pointer != loop.Pointer ||
                    loop.next.vertex == null || !loop.edge.ContainsVertices(loop.vertex, loop.next.vertex))
                    return "a face's corners are not correctly linked";
                if (loop.radialNext == null || loop.radialPrev == null || !loops.Contains(loop.radialNext.Pointer) || !loops.Contains(loop.radialPrev.Pointer) ||
                    loop.radialNext.edge?.Pointer != loop.edge.Pointer || loop.radialPrev.edge?.Pointer != loop.edge.Pointer ||
                    loop.radialNext.radialPrev?.Pointer != loop.Pointer || loop.radialPrev.radialNext?.Pointer != loop.Pointer)
                    return "a corner is not correctly linked to its edge";
                if (loop.thickenEdge != null && (!edges.Contains(loop.thickenEdge.Pointer) || !loop.thickenEdge.ContainsVertex(loop.vertex)))
                    return "a corner's thickening edge no longer belongs to its point";
            }
            if (loop?.Pointer != first?.Pointer) return "a face's corner cycle does not close";
        }
        return usedLoops.SetEquals(loops) ? null : "the mesh contains corners outside its faces";
    }

    // ---------- the tools ----------

    static MeshPlans.FlattenMode flattenMode;
    static readonly string[] FlattenNames = { "Mode: best-fit plane", "Mode: level (same height)", "Mode: sideways (same X)", "Mode: lengthways (same Z)" };
    static float insetMm = 50, bevelMm = 30, flatAngle = 5, radiusMm = 500;
    static float smoothMm = 30, filletRadiusMm = 30;
    static int smoothSegments = 4, filletSegments = 4, splitSections = 2;
    static bool splitOtherDirection;
    static IntPtr lastSplitMesh;
    static List<Num[]> lastSplitFaces = new();
    static bool proportional, halfGrid;

    static void Flatten(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        var mode = flattenMode;
        Run(e, "Flatten", mesh =>
        {
            var v = new View(mesh);
            var points = new HashSet<int>(v.SelectedPoints);
            foreach (int f in v.SelectedFaces) points.UnionWith(v.Corners[f]);
            if (mirror && mode == MeshPlans.FlattenMode.Sideways)
            {
                // Both sides selected: each side gets one x (the other side's is its mirror), not one x for both.
                var pairs = MeshPlans.Twins(v.Pos, points, Tolerance);
                points.ExceptWith(points.Where(p => v.Pos[p].X < 0 && pairs.TryGetValue(p, out int t) && t != p && points.Contains(t)).ToList());
            }
            Num? normal = v.SelectedFaces.Count > 0 ? v.SelectedFaces.Aggregate(Num.Zero, (s, f) => s + HoleRing.Normal(v.Corners[f].Select(p => v.Pos[p]).ToList())) : null;
            var moved = MeshPlans.Flatten(v.Pos, points, mode, normal);
            if (moved.Count == 0) return (false, "select three or more points (two to level them)");
            if (mirror)
                foreach (var (p, twin) in MeshPlans.Twins(v.Pos, points, Tolerance))
                    if (!points.Contains(twin)) moved[twin] = new Num(-moved[p].X, moved[p].Y, moved[p].Z);
            if (MeshPlans.Folds(v.Pos, v.Corners, moved) is string folds) return (false, "not done: " + folds);
            foreach (var (p, at) in moved) v.Verts[p].position = new Vector3(at.X, at.Y, at.Z);
            mesh.MarkDirty(MeshDirtyFlags.All);
            return (true, $"flattened {moved.Count} points");
        });
    }

    static void LoopCut(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Edge) { e.operations.NotifyError("Loop cut: switch to Edges and select an edge"); return; }
        bool mirror = e.meshEditor.Symmetry;
        Run(e, "Loop cut", mesh =>
        {
            var v = new View(mesh);
            if (v.SelectedEdges.Count == 0) return (false, "select an edge first");
            return Apply(mesh, v, MeshPlans.LoopCut(v.Pos, v.Corners, WithTwins(v, v.SelectedEdges, mirror)), "loop cut");
        });
    }

    static void Inset(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        float width = insetMm / 1000;
        Run(e, "Inset", mesh =>
        {
            var v = new View(mesh);
            return Apply(mesh, v, MeshPlans.Inset(v.Pos, v.Corners, WithTwins(v, new HashSet<int>(v.SelectedFaces), mirror), width), "inset");
        });
    }

    static void Bevel(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Edge) { e.operations.NotifyError("Bevel: switch to Edges and select edges"); return; }
        bool mirror = e.meshEditor.Symmetry;
        float width = bevelMm / 1000;
        Run(e, "Bevel", mesh =>
        {
            var v = new View(mesh);
            if (v.SelectedEdges.Count == 0) return (false, "select edges first");
            return Apply(mesh, v, MeshPlans.Bevel(v.Pos, v.Corners, WithTwins(v, v.SelectedEdges, mirror), width), "bevel", preserveBevelEdges: true);
        });
    }

    static void SmoothEdge(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Edge) { e.operations.NotifyError("Smooth Edge: switch to Edges and select edges"); return; }
        bool mirror = e.meshEditor.Symmetry;
        float width = smoothMm / 1000; int segments = smoothSegments;
        Run(e, "Smooth Edge", mesh =>
        {
            var v = new View(mesh);
            var edges = WithTwins(v, v.SelectedEdges, mirror).Select(e => FaceMerge.Key(e.Item1,e.Item2)).ToHashSet();
            return Apply(mesh, v, MeshPlans.Bevel(v.Pos, v.Corners, edges, width, segments), "smooth edge", preserveBevelEdges: true, roundedEdges: edges);
        });
    }

    static void Fillet(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Edge) { e.operations.NotifyError("Fillet: switch to Edges and select edges"); return; }
        bool mirror = e.meshEditor.Symmetry;
        float radius = filletRadiusMm / 1000; int segments = filletSegments;
        Run(e, "Fillet", mesh =>
        {
            var v = new View(mesh);
            var edges = WithTwins(v, v.SelectedEdges, mirror).Select(edge => FaceMerge.Key(edge.Item1, edge.Item2)).ToHashSet();
            return Apply(mesh, v, EdgeFillet.Round(v.Pos, v.Corners, edges, radius, segments), "fillet", preserveBevelEdges: true, roundedEdges: edges);
        });
    }

    static void SplitEdges(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Face) { e.operations.NotifyError("Split Face: switch to Faces and select the face to split"); return; }
        bool mirror = e.meshEditor.Symmetry; int sections = splitSections, side = splitOtherDirection ? 1 : 0;
        Run(e, "Split Face", mesh =>
        {
            var v = new View(mesh);
            var scope = WithTwins(v, new HashSet<int>(v.SelectedFaces), mirror);
            if (scope.Count == 0) return (false, "select a face first");
            var edges = scope.Select(f => (v.Corners[f][side], v.Corners[f][(side+1)%v.Corners[f].Length])).ToList();
            return Apply(mesh, v, EdgeSubdivision.Split(v.Pos, v.Corners, edges, sections, scope), "split face", preserveBevelEdges: true,
                afterApply: made =>
                {
                    lastSplitMesh = mesh.Pointer;
                    lastSplitFaces = made.Where(m => scope.Contains(m.Source)).Select(m => HoleQuality.Corners(m.Face)).ToList();
                });
        });
    }

    static void SelectBetweenSplits(PlateStructureEditor e)
    {
        Run(e, "Select between splits", mesh =>
        {
            if (lastSplitMesh != mesh.Pointer || lastSplitFaces.Count == 0) return (false, "split a face on this structure first");
            var found = new List<Face>();
            foreach (var face in mesh.faces)
                if (SplitFaceSelection.Matches(HoleQuality.Corners(face), lastSplitFaces)) found.Add(face);
            if (found.Count != lastSplitFaces.Count) return (false, "the last split was changed or undone; split again or redo it first");
            foreach (var face in mesh.faces) face.DisableFlag(ElementFlags.Selected);
            foreach (var edge in mesh.edges) edge.DisableFlag(ElementFlags.Selected);
            foreach (var vertex in mesh.vertices) vertex.DisableFlag(ElementFlags.Selected);
            e.meshEditor.SelectType = MeshEditType.Face;
            foreach (var face in found)
            {
                face.EnableFlag(ElementFlags.Selected);
                var l = face.firstLoop;
                for (int k=0;k<face.vertexCount;k++,l=l.next)
                { l.vertex.EnableFlag(ElementFlags.Selected); l.edge.EnableFlag(ElementFlags.Selected); }
            }
            mesh.MarkDirty(MeshDirtyFlags.All);
            return (true, $"selected all {found.Count} faces between the last split lines");
        });
    }

    static void SelectFlat(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        float angle = flatAngle;
        Run(e, "Select linked flat", mesh =>
        {
            var v = new View(mesh);
            if (v.SelectedFaces.Count == 0) return (false, "select a face first");
            var found = MeshPlans.LinkedFlat(v.Pos, v.Corners, WithTwins(v, new HashSet<int>(v.SelectedFaces), mirror), angle);
            foreach (int f in found)
            {
                v.Faces[f].EnableFlag(ElementFlags.Selected);
                var l = v.Faces[f].firstLoop;
                for (int k = 0; k < v.Faces[f].vertexCount; k++, l = l.next) { l.vertex.EnableFlag(ElementFlags.Selected); l.edge.EnableFlag(ElementFlags.Selected); }
            }
            mesh.MarkDirty(MeshDirtyFlags.All);
            return (true, $"selected {found.Count} faces ({found.Count - v.SelectedFaces.Count} more)");
        });
    }

    static int bridgeCuts = 4;
    static float bridgeSmooth = 100; // percent: 0 straight across, 100 about round
    static float mirrorMm = 5;
    static MeshPlans.MirrorKeep mirrorKeep;
    static readonly string[] KeepNames = { "Keep: meet halfway", "Keep: right side", "Keep: left side" };

    static Num Image(Num p) => new(-p.X, p.Y, p.Z);

    /// Bridge (Blender's Bridge Edge Loops): two chains of selected edges joined by a strip of faces.
    static void Bridge(PlateStructureEditor e)
    {
        if (e.meshEditor.SelectType != MeshEditType.Edge) { e.operations.NotifyError("Bridge: switch to Edges and select two chains of edges"); return; }
        bool mirror = e.meshEditor.Symmetry;
        int cuts = bridgeCuts;
        float smooth = bridgeSmooth / 100;
        Run(e, "Bridge", mesh =>
        {
            var v = new View(mesh);
            if (v.SelectedEdges.Count == 0) return (false, "select two chains of edges first (the open edges of two plates)");
            var twins = mirror ? MeshPlans.Twins(v.Pos, Enumerable.Range(0, v.Pos.Count), Tolerance) : null;
            // The strip's open sides are new open edges on purpose, so gaps aren't checked; faces laid over each other are.
            return Apply(mesh, v, MeshPlans.Bridge(v.Pos, v.Corners, v.SelectedEdges, cuts, smooth, twins), $"bridge ({cuts} cuts, {smooth:P0} smooth)", gaps: false);
        });
    }

    /// Circle (LoopTools' Circle): the selected points spread evenly round a true circle.
    static void Circle(PlateStructureEditor e)
    {
        bool mirror = e.meshEditor.Symmetry;
        Run(e, "Circle", mesh =>
        {
            var v = new View(mesh);
            var points = new HashSet<int>(v.SelectedPoints);
            foreach (int f in v.SelectedFaces) points.UnionWith(v.Corners[f]);
            if (points.Count < 3) return (false, "select three or more points round a loop (Alt+click an edge selects a loop)");

            // Helper to get plate/face normal for a given set of points:
            Num? NormalOf(ICollection<int> pts)
            {
                var matchingFaces = v.SelectedFaces.Where(f => v.Corners[f].Count(pts.Contains) >= 3).ToList();
                if (matchingFaces.Count == 0 && v.SelectedFaces.Count > 0)
                    matchingFaces = v.SelectedFaces.Where(f => v.Corners[f].Any(pts.Contains)).ToList();
                if (matchingFaces.Count > 0)
                {
                    var sum = matchingFaces.Aggregate(Num.Zero, (s, f) => s + HoleRing.Normal(v.Corners[f].Select(p => v.Pos[p]).ToList()));
                    if (sum.LengthSquared() > 1e-12f) return Num.Normalize(sum);
                }
                var plateFace = v.Corners
                    .Where(c => c.Count(pts.Contains) >= 3)
                    .Select(c => HoleRing.Normal(c.Select(p => v.Pos[p]).ToList()))
                    .FirstOrDefault(n => n.LengthSquared() > 1e-12f);
                if (plateFace.LengthSquared() > 1e-12f)
                    return Num.Normalize(plateFace);
                return null;
            }

            var moved = new Dictionary<int, Num>();
            if (mirror)
            {
                var twins = MeshPlans.Twins(v.Pos, points, Tolerance);
                var posSide = points.Where(p => v.Pos[p].X > Tolerance).ToList();
                var negSide = points.Where(p => v.Pos[p].X < -Tolerance).ToList();

                // Check if any selected point or edge connecting selected points crosses or lies on the centerline
                bool edgesCrossCenter = v.Corners.Any(c =>
                {
                    for (int k = 0; k < c.Length; k++)
                    {
                        int p1 = c[k], p2 = c[(k + 1) % c.Length];
                        if (points.Contains(p1) && points.Contains(p2))
                        {
                            if ((v.Pos[p1].X > Tolerance && v.Pos[p2].X < -Tolerance) ||
                                (v.Pos[p1].X < -Tolerance && v.Pos[p2].X > Tolerance))
                                return true;
                        }
                    }
                    return false;
                }) || v.SelectedEdges.Any(ed =>
                    (v.Pos[ed.A].X > Tolerance && v.Pos[ed.B].X < -Tolerance) ||
                    (v.Pos[ed.A].X < -Tolerance && v.Pos[ed.B].X > Tolerance));

                bool crossesCenter = points.Any(p => Math.Abs(v.Pos[p].X) <= Tolerance) || edgesCrossCenter;

                if (!crossesCenter && posSide.Count >= 3)
                {
                    // Positive side (or both sides selected): solve positive side, mirror to twins
                    var movedPos = MeshPlans.Circle(v.Pos, posSide, normal: NormalOf(posSide));
                    if (movedPos.Count == 0) return (false, "these points lie along a line: select points round a loop");
                    foreach (var (p, at) in movedPos)
                    {
                        moved[p] = at;
                        if (twins.TryGetValue(p, out int twin)) moved[twin] = Image(at);
                    }
                }
                else if (!crossesCenter && negSide.Count >= 3)
                {
                    // Only negative side selected: solve negative side, mirror to twins
                    var movedNeg = MeshPlans.Circle(v.Pos, negSide, normal: NormalOf(negSide));
                    if (movedNeg.Count == 0) return (false, "these points lie along a line: select points round a loop");
                    foreach (var (p, at) in movedNeg)
                    {
                        moved[p] = at;
                        if (twins.TryGetValue(p, out int twin)) moved[twin] = Image(at);
                    }
                }
                else
                {
                    // A single loop crossing the vehicle's centerline (e.g. turret ring on the roof, hatch on glacis):
                    // Normal must be symmetric (nX = 0) so the circle doesn't tilt sideways across the hull.
                    var faceNorm = NormalOf(points);
                    Num? symNormal = faceNorm is { } n && (n.Y != 0 || n.Z != 0) ? Num.Normalize(new Num(0, n.Y, n.Z)) : null;
                    var movedMid = MeshPlans.Circle(v.Pos, points, normal: symNormal);
                    if (movedMid.Count == 0) return (false, "these points lie along a line: select points round a loop");
                    foreach (var (p, at) in movedMid) moved[p] = at;
                    var midCenter = points.Aggregate(Num.Zero, (s, p) => s + v.Pos[p]) / points.Count;
                    midCenter.X = 0;
                    float midRadius = points.Average(p => Num.Distance(moved[p], midCenter));
                    foreach (var (p, twin) in twins)
                    {
                        if (twin == p) { moved[p] = new Num(0, moved[p].Y, moved[p].Z); continue; }
                        if (!points.Contains(twin)) { moved[twin] = Image(moved[p]); continue; }
                        if (p < twin) continue;
                        var meet = (moved[p] + Image(moved[twin])) / 2;
                        var dir = meet - midCenter;
                        dir.X = (moved[p].X - moved[twin].X) / 2;
                        if (dir.LengthSquared() > 1e-12f)
                        {
                            var proj = midCenter + midRadius * Num.Normalize(dir);
                            moved[p] = proj;
                            moved[twin] = Image(proj);
                        }
                        else
                        {
                            moved[p] = meet;
                            moved[twin] = Image(meet);
                        }
                    }
                }
            }
            else
            {
                var movedOnce = MeshPlans.Circle(v.Pos, points, normal: NormalOf(points));
                if (movedOnce.Count == 0) return (false, "these points lie along a line: select points round a loop");
                foreach (var (p, at) in movedOnce) moved[p] = at;
            }
            if (MeshPlans.Folds(v.Pos, v.Corners, moved) is string folds) return (false, "not done: " + folds);
            foreach (var (p, at) in moved) v.Verts[p].position = new Vector3(at.X, at.Y, at.Z);
            mesh.MarkDirty(MeshDirtyFlags.All);
            var c = points.Aggregate(Num.Zero, (s, p) => s + moved[p]) / points.Count;
            return (true, $"{points.Count} points spread round a circle {Num.Distance(moved[points.First()], c) * 2000:0} mm across" + (moved.Count > points.Count ? $", {moved.Count - points.Count} mirrored" : ""));
        });
    }

    /// Fix mirror: points nearly each other's mirror image made exactly so (and near-centre points put on the middle),
    /// so the editor's Mirror finds them again. The points left with no mirror image are selected, to show where the
    /// two sides differ.
    static void FixMirror(PlateStructureEditor e)
    {
        float tolerance = mirrorMm / 1000;
        var keep = mirrorKeep;
        Run(e, "Fix mirror", mesh =>
        {
            var v = new View(mesh);
            var start = v.SelectedPoints.Count > 0 ? v.SelectedPoints.ToList() : Enumerable.Range(0, v.Pos.Count).ToList();
            var (moved, unmatched) = MeshPlans.FixMirror(v.Pos, start, tolerance, keep);
            if (MeshPlans.Folds(v.Pos, v.Corners, moved) is string folds) return (false, "not done: " + folds);
            foreach (var (p, at) in moved) v.Verts[p].position = new Vector3(at.X, at.Y, at.Z);
            if (unmatched.Count > 0)
            {
                // Show where: those points selected, nothing else.
                var vertices = mesh.vertices;
                for (int i = 0; i < vertices.Count; i++) vertices[i].DisableFlag(ElementFlags.Selected);
                var edges = mesh.edges;
                for (int i = 0; i < edges.Count; i++) edges[i].DisableFlag(ElementFlags.Selected);
                var faces = mesh.faces;
                for (int i = 0; i < faces.Count; i++) faces[i].DisableFlag(ElementFlags.Selected);
                foreach (int p in unmatched) v.Verts[p].EnableFlag(ElementFlags.Selected);
            }
            mesh.MarkDirty(MeshDirtyFlags.All);
            string scope = v.SelectedPoints.Count > 0 ? "selected points" : "whole shape";
            return (true, $"{scope}: {moved.Count} points moved to match (within {mirrorMm:0.#} mm)" +
                          (unmatched.Count == 0 ? ", every point has its mirror image" : $", {unmatched.Count} have no mirror image and are selected: the sides differ there (merged, split or filled on one side only)"));
        });
    }

    // ---------- keys (from DesignEditor.Update) ----------

    internal static void Keys()
    {
        var keys = Keyboard.current;
        if (keys == null || Typing() || DrawingSheet.Capturing || PhotoShot.Capturing) return;
        var e = Hotkeys.Current;
        if (e != null && halfGrid) KeepHalfGrid(e);
        bool ctrl = keys.ctrlKey.isPressed;
        if (Keybinds.Pressed("shadows")) ToggleShadows();
        if (Keybinds.Pressed("flashlight")) ToggleFlashlight();
        if (Keybinds.Pressed("fullbright")) ToggleFullbright();
        AimFlashlight();
        PlaceFills();
        PlaceHeadlight();
        // Ctrl turns a view the other way round (unless that view's own key needs Ctrl).
        bool Other(string id) => ctrl && !Keybinds.NeedsCtrl(id);
        if (Keybinds.Pressed("front")) LookFrom(Other("front") ? Vector3.forward : Vector3.back);
        if (Keybinds.Pressed("side")) LookFrom(Other("side") ? Vector3.right : Vector3.left);
        if (Keybinds.Pressed("top")) LookFrom(Other("top") ? Vector3.up : Vector3.down);
        if (Keybinds.Pressed("opposite")) LookFrom(-(held ?? shown));
        // Plain keys only from here: Ctrl, Alt and Shift with them belong to the game (unless a key is bound with them).
        bool Plain(string id) => Keybinds.Pressed(id) && (Keybinds.NeedsCtrl(id) || !(ctrl || keys.altKey.isPressed || keys.shiftKey.isPressed));
        if (Plain("ortho")) ToggleOrtho();
        bool zoomIn = Plain("zoomIn"), zoomOut = !zoomIn && Plain("zoomOut");
        if (ortho && (zoomIn || zoomOut))
        {
            orthoZoom = Math.Clamp(orthoZoom * (zoomIn ? 1.25f : 0.8f), 0.1f, 10);
            e?.RequestRedraw(); // the panel's zoom slider follows
        }
        if (e == null) return;
        if (Plain("flatten")) Flatten(e);
        else if (Plain("loopCut")) LoopCut(e);
        else if (Plain("inset")) Inset(e);
        else if (Plain("bevel")) Bevel(e);
        else if (Plain("selectFlat")) SelectFlat(e);
        else if (Plain("proportional")) { proportional = !proportional; e.RequestRedraw(); Plugin.ModLog.LogInfo($"Proportional editing {(proportional ? "on" : "off")}"); }
    }

    /// Typing in a text box (a part's name): the keys are letters then, not tools.
    internal static bool Typing()
    {
        var go = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
        return go != null && (go.GetComponent<TMPro.TMP_InputField>()?.isFocused == true || go.GetComponent<UnityEngine.UI.InputField>()?.isFocused == true);
    }

    // ---------- 0.5 mm grid ----------

    static float? gridBefore;
    static MeshEditor? gridOwner;

    static void RestoreHalfGrid()
    {
        try { if (gridOwner != null && gridBefore is { } before) gridOwner.GridSize = before; }
        finally { gridOwner = null; gridBefore = null; }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MeshEditor), nameof(MeshEditor.Release))]
    static void ReleaseGrid(MeshEditor __instance) => Ui.Guard("0.5 mm grid", () =>
    {
        if (gridOwner?.Pointer == __instance.Pointer) RestoreHalfGrid();
    });

    /// Half a millimetre, in whatever unit the game's grid size is in (it shows millimetres; it may store metres).
    static void KeepHalfGrid(PlateStructureEditor e)
    {
        if (gridOwner != null && gridOwner.Pointer != e.meshEditor.Pointer) RestoreHalfGrid();
        float now = e.meshEditor.GridSize;
        float half = (gridBefore ?? now) >= 0.01f ? 0.5f : 0.0005f;
        gridOwner = e.meshEditor;
        gridBefore ??= now;
        if (Math.Abs(now - half) > half * 0.01f) e.meshEditor.GridSize = half;
    }

    // ---------- shadows (F5) ----------

    // Lights whose shadows are off, with what they had, to give it back exactly.
    static readonly List<(Light Light, LightShadows Was)> shadowless = new();

    static void ToggleShadows() => Ui.Guard("Shadows", () =>
    {
        if (shadowless.Count > 0)
        {
            foreach (var (light, was) in shadowless)
                if (light != null)
                {
                    light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.EnableShadows(true);
                    light.shadows = was;
                }
            DesignEditor.Instance?.Say($"Shadows back on ({shadowless.Count} lights)", 3);
            shadowless.Clear();
            if (headlight != null) UnityEngine.Object.Destroy(headlight);
            headlight = null;
            return;
        }
        Light? sun = null;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            if (o.TryCast<Light>() is { } light && light.shadows != LightShadows.None)
            {
                if (light.type == LightType.Directional && (sun == null || light.intensity > sun.intensity)) sun = light;
                shadowless.Add((light, light.shadows));
                light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.EnableShadows(false);
                light.shadows = LightShadows.None;
            }
        if (sun != null) headlight = Headlight(sun);
        DesignEditor.Instance?.Say(shadowless.Count > 0 ? $"Shadows off ({shadowless.Count} lights), {Keybinds.Shown("shadows")} to turn them back on" : "No light casting shadows found", 3);
        Plugin.ModLog.LogInfo($"Shadows off on {shadowless.Count} lights");
    });

    // With shadows off, a headlight lights whatever the camera looks at, so the sides turned from the sun aren't black.
    static GameObject? headlight;
    const float HeadlightShare = 0.6f; // of the sun's strength

    /// A new shadowless light with the sun's colour and part of its strength (a new object, not a copy of the sun's,
    /// so none of the game's scripts on the sun run twice). Turned with the camera every frame by Keys.
    static GameObject Headlight(Light sun)
    {
        // A point light (the game's renderer draws only one directional light: the sun's).
        var go = new GameObject("Quality of Life headlight");
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = sun.color;
        light.useColorTemperature = sun.useColorTemperature;
        light.colorTemperature = sun.colorTemperature;
        light.shadows = LightShadows.None;
        var hd = LikeTheSun(light, sun);
        hd.EnableShadows(false);
        light.shadows = LightShadows.None;
        headLux = SunLux(sun) * HeadlightShare;
        headlight = go;
        PlaceHeadlight();
        Plugin.ModLog.LogInfo($"Shadows off: headlight at the camera, {headLux:0} lux on what it looks at ({HeadlightShare:P0} of the sun '{sun.name}')");
        return go;
    }

    static float headLux;

    /// At the camera (in orthographic view where a normal view's camera would be, not 100 m back), as strong as it takes
    /// to put the set share of the sun on what the camera looks at.
    static void PlaceHeadlight()
    {
        if (headlight == null || Camera.main is not { } cam) return;
        float distance = Math.Max(1, orbit?.TargetDistance ?? 10);
        headlight.transform.position = cam.transform.position + (ortho && orthoWhole ? cam.transform.forward * PullBack : Vector3.zero);
        headlight.GetComponent<Light>().range = 4 * distance + 10;
        headlight.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.SetIntensity(headLux * distance * distance, UnityEngine.Rendering.LightUnit.Candela);
    }

    /// A new light lights what the sun lights: the same light layers (HDRP lights only reach objects on their layers,
    /// and a new light starts on the default one). Returns its HDRP settings.
    static UnityEngine.Rendering.HighDefinition.HDAdditionalLightData LikeTheSun(Light light, Light sun)
    {
        light.cullingMask = sun.cullingMask;
        light.renderingLayerMask = sun.renderingLayerMask;
        var hd = light.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() ?? light.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
        if (sun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } sunHd) hd.lightlayersMask = sunHd.lightlayersMask;
        // Not a sun in the sky: the sky dims a directional light near the horizon and blacks it out below it (so light
        // from below, from the sides and back did nearly nothing). Ours light the vehicle at full strength from anywhere.
        hd.interactsWithSky = false;
        return hd;
    }

    /// How many directional lights the renderer draws at once (lights past it are left out), 16 if it won't say.
    static int MaxDirectional()
    {
        try
        {
            if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline?.TryCast<UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset>() is { } asset)
                return asset.currentPlatformRenderPipelineSettings.lightLoopSettings.maxDirectionalLightsOnScreen;
        }
        catch (Exception ex) { Plugin.ModLog.LogWarning($"Fullbright: couldn't read the directional light limit: {ex.Message}"); }
        return 16;
    }

    static float SunLux(Light sun) => sun.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>() is { } hd ? hd.intensity : sun.intensity;

    /// The brightest directional light that isn't one of ours.
    static Light? Sun()
    {
        Light? sun = null;
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Light>()))
            if (o.TryCast<Light>() is { } light && light.type == LightType.Directional && !light.name.StartsWith("Quality of Life") && (sun == null || light.intensity > sun.intensity)) sun = light;
        return sun;
    }

    // ---------- fullbright (F7) ----------

    static readonly List<GameObject> fills = new();
    static bool fullbrightShadows; // whether fullbright turned the shadows off (then it turns them back on)
    internal static bool FullbrightOn { get { fills.RemoveAll(f => f == null); return fills.Count > 0; } }
    static float FillShare => (Plugin.FullbrightPercent?.Value ?? 25) / 100; // of the sun's strength, per light
    // From every side and every corner: 14 lights (with the sun, within the 16 directional lights HDRP draws at once).
    static readonly Vector3[] FillFrom = new[] { Vector3.down, Vector3.up, Vector3.left, Vector3.right, Vector3.forward, Vector3.back }
        .Concat(Enumerable.Range(0, 8).Select(i => new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1).normalized)).ToArray();

    /// The fill lights follow the brightness slider while on.
    static void FillBrightness() { fillsAt = -10; PlaceFills(); }

    /// Even light from all six sides with shadows off, so every face shows clearly whichever way it faces.
    internal static void ToggleFullbright() => Ui.Guard("Fullbright", () =>
    {
        fills.RemoveAll(f => f == null); // gone with a scene change
        if (fills.Count > 0)
        {
            foreach (var f in fills) UnityEngine.Object.Destroy(f);
            fills.Clear();
            if (fullbrightShadows && shadowless.Count > 0) ToggleShadows();
            fullbrightShadows = false;
            DesignEditor.Instance?.Say("Fullbright off", 2);
            return;
        }
        var sun = Sun();
        if (sun == null) { DesignEditor.Instance?.Say("Fullbright: no sun in this scene to match", 3); return; }
        if (shadowless.Count == 0)
        {
            ToggleShadows();
            fullbrightShadows = true;
            if (headlight != null) { UnityEngine.Object.Destroy(headlight); headlight = null; } // the fills light every side already
        }
        // Point lights round the vehicle (the game's renderer draws only one directional light, the sun's).
        foreach (var dir in FillFrom)
        {
            var go = new GameObject("Quality of Life fill light");
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = sun.color;
            light.useColorTemperature = sun.useColorTemperature;
            light.colorTemperature = sun.colorTemperature;
            light.shadows = LightShadows.None;
            var hd = LikeTheSun(light, sun);
            hd.EnableShadows(false);
            light.shadows = LightShadows.None;
            fills.Add(go);
        }
        fillsAt = -10;
        PlaceFills();
        DesignEditor.Instance?.Say($"Fullbright on: even light from every side, no shadows ({Keybinds.Shown("fullbright")} to turn off)", 3);
        Plugin.ModLog.LogInfo($"Fullbright on: {fills.Count} point lights round the vehicle, {SunLux(sun) * FillShare:0} lux each on it ({FillShare:P0} of the sun); the renderer draws {MaxDirectional()} directional light(s), so none of those");
    });

    static float fillsAt = -10;

    /// The fill lights round the vehicle, five times its size away (so each side is lit evenly), each as strong as it
    /// takes to put the set share of the sun on the vehicle (candela = lux x distance squared). Again every 2 s: the
    /// vehicle grows as it's built.
    static void PlaceFills()
    {
        fills.RemoveAll(f => f == null);
        if (fills.Count == 0 || Time.unscaledTime - fillsAt < 2) return;
        fillsAt = Time.unscaledTime;
        var sun = Sun();
        if (sun == null || VehicleBounds() is not { } box) return;
        float radius = Math.Max(0.5f, box.extents.magnitude), distance = 5 * radius + 5;
        for (int i = 0; i < fills.Count && i < FillFrom.Length; i++)
        {
            fills[i].transform.position = box.center - FillFrom[i] * distance; // it shines along FillFrom[i]
            fills[i].GetComponent<Light>().range = 3 * (distance + radius);
            fills[i].GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>()?.SetIntensity(SunLux(sun) * FillShare * distance * distance, UnityEngine.Rendering.LightUnit.Candela);
        }
    }

    /// The box round every part of the vehicle being edited, measured at most once a second (every part's renderers:
    /// not cheap on a big tank), shared by the fill lights and the plain backdrop.
    internal static Bounds? VehicleBounds() => Boxes().All;

    /// The same without the antennas: the vehicle's own size, as measured.
    internal static Bounds? BodyBounds() => Boxes().Body;

    static (Bounds? All, Bounds? Body) Boxes()
    {
        var target = DesignEditor.Instance?.Core?.Target?.Pointer ?? IntPtr.Zero;
        if (vehicleBoxOwner == target && Time.unscaledTime - vehicleBoxAt < 1) return vehicleBox;
        vehicleBoxOwner = target;
        vehicleBoxAt = Time.unscaledTime;
        var aerials = AntennaRenderers();
        var counted = new HashSet<IntPtr>();
        Bounds? all = null, body = null;
        foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
            foreach (var r in part.GetComponentsInChildren<Renderer>())
            {
                if (!counted.Add(r.Pointer) || !Drawn(r)) continue;
                var b = r.bounds;
                all = Grow(all, b);
                if (!aerials.Contains(r.Pointer)) body = Grow(body, b);
            }
        return vehicleBox = (all, body);
    }

    /// Drawn: switched on, and not a shadow-only stand-in. The others have a size too but aren't seen, so they count in
    /// no measurement.
    internal static bool Drawn(Renderer r) => r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;

    /// Once per orthographic view: which renderer sets each side of the measured box, and anything drawn close by that
    /// isn't part of the vehicle (so a measurement that doesn't fit what's on screen can be told apart).
    static void LogMeasuredBox(Bounds box)
    {
        var aerials = AntennaRenderers();
        var own = new HashSet<IntPtr>();
        var counted = new List<Renderer>();
        foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
            foreach (var r in part.GetComponentsInChildren<Renderer>(true))
                if (own.Add(r.Pointer) && Drawn(r) && r.gameObject.activeInHierarchy && !aerials.Contains(r.Pointer)) counted.Add(r);
        if (counted.Count == 0) return;
        string Say(Renderer r) => $"{(r.transform.parent != null ? r.transform.parent.name + "/" : "")}{r.name} {r.bounds.min:F2}..{r.bounds.max:F2}";
        var sides = new (string Side, Func<Renderer, float> By, bool Low)[]
        {
            ("left", r => r.bounds.min.x, true), ("right", r => r.bounds.max.x, false), ("bottom", r => r.bounds.min.y, true),
            ("top", r => r.bounds.max.y, false), ("back", r => r.bounds.min.z, true), ("front", r => r.bounds.max.z, false),
        };
        var near = new Bounds(box.center, box.size + Vector3.one);
        var others = new List<string>();
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
            if (o.TryCast<Renderer>() is { } r && !own.Contains(r.Pointer) && Drawn(r) && near.Intersects(r.bounds) && others.Count < 12) others.Add(Say(r));
        Plugin.ModLog.LogInfo($"Measurements: box {box.min:F2}..{box.max:F2} from {counted.Count} renderers ({own.Count - counted.Count} of the vehicle's not counted: off, shadow only or antennas); " +
                              string.Join("; ", sides.Select(s => $"{s.Side} {Say(s.Low ? counted.OrderBy(s.By).First() : counted.OrderByDescending(s.By).First())}")) +
                              $". Drawn nearby, not the vehicle's: {(others.Count > 0 ? string.Join("; ", others) : "nothing")}");
    }

    static Bounds Grow(Bounds? box, Bounds b)
    {
        if (box is not { } grown) return b;
        grown.Encapsulate(b);
        return grown;
    }

    /// The antennas' renderers: a whip metres tall is left out of the vehicle's measured size (and off the drawing sheet).
    /// The game's antenna parts, and anything else standing up tall and thin (a decorative whip, or one built from an
    /// add-on): over AntennaTall high and under AntennaThin across both ways.
    internal static HashSet<IntPtr> AntennaRenderers()
    {
        var found = new HashSet<IntPtr>();
        foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
        {
            bool antenna = DesignEditor.Each(part.Components).Any(c => c?.TryCast<Sprocket.Vehicles.RadioSystems.Antenna>() != null);
            foreach (var r in part.GetComponentsInChildren<Renderer>())
            {
                var size = r.bounds.size;
                if (antenna || (size.y > AntennaTall && size.x < AntennaThin && size.z < AntennaThin)) found.Add(r.Pointer);
            }
        }
        return found;
    }

    const float AntennaTall = 0.5f, AntennaThin = 0.08f; // metres

    // ---------- mouse flashlight (F6) ----------

    static GameObject? flashlight;
    static Light? flashLight;
    static UnityEngine.Rendering.HighDefinition.HDAdditionalLightData? flashHd;
    static float flashLux;
    static float FlashShare => (Plugin.FlashlightPercent?.Value ?? 80) / 100f; // of the sun's light, landing on what the mouse points at

    static void ToggleFlashlight() => Ui.Guard("Flashlight", () =>
    {
        if (flashlight != null)
        {
            UnityEngine.Object.Destroy(flashlight);
            flashlight = null;
            DesignEditor.Instance?.Say("Flashlight off", 2);
            return;
        }
        var sun = Sun();
        if (sun == null) { DesignEditor.Instance?.Say("Flashlight: no sun in this scene to match", 3); return; }
        flashlight = new GameObject("Quality of Life flashlight");
        flashLight = flashlight.AddComponent<Light>();
        flashLight.type = LightType.Spot;
        flashLight.spotAngle = 40;
        flashLight.color = sun.color;
        flashLight.useColorTemperature = sun.useColorTemperature;
        flashLight.colorTemperature = sun.colorTemperature;
        flashLight.shadows = LightShadows.None;
        flashHd = LikeTheSun(flashLight, sun);
        flashHd.EnableShadows(false);
        flashLight.shadows = LightShadows.None;
        flashLux = SunLux(sun);
        AimFlashlight();
        DesignEditor.Instance?.Say($"Flashlight on: it points where the mouse points ({Keybinds.Shown("flashlight")} to turn off)", 3);
        Plugin.ModLog.LogInfo($"Flashlight on ({FlashShare:P0} of the sun's {flashLux:0} lux where it lands)");
    });

    /// From the camera along the mouse, as strong as needed to put a set share of the sun's light on what it hits
    /// (candela = lux × distance²), so it's as bright near or far.
    static void AimFlashlight()
    {
        if (flashlight == null || flashLight == null || flashHd == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        var ray = Mouse.current is { } mouse ? cam.ScreenPointToRay(mouse.position.ReadValue()) : cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
        float distance = Physics.Raycast(ray, out var hit, 2000f) ? hit.distance : float.MaxValue;
        if (distance == float.MaxValue) distance = (orbit?.TargetDistance ?? 10) + (ortho && orthoWhole ? PullBack : 0);
        var from = ray.origin;
        if (ortho)
        {
            // The orthographic camera stands far back: shine from where a normal view would, or the beam covers everything.
            float near = Math.Max(0.5f, orbit?.TargetDistance ?? 10);
            if (distance > near) { from += ray.direction * (distance - near); distance = near; }
        }
        flashlight.transform.SetPositionAndRotation(from, Quaternion.LookRotation(ray.direction));
        flashLight.range = distance * 1.5f + 5;
        flashHd.SetIntensity(flashLux * FlashShare * distance * distance, UnityEngine.Rendering.LightUnit.Candela);
    }

    // ---------- orthographic view ----------

    static bool ortho, orthoWhole = true, orthoLock = true, orthoLogged;
    static int arrowDraws; // since orthographic view went on or off: the size is logged once it has settled
    static float orthoZoom = 1; // on top of the orbit distance, which stops at the game's closest zoom
    static float? farBefore;
    static Camera? orthoOwner;
    static bool originalOrthographic;
    static float originalOrthoSize;
    const float PullBack = 100; // metres the camera steps back in orthographic view, so it never cuts into the vehicle
    static Sprocket.OrbitalMovementController? orbit;

    /// The editor closed: back to the game's own camera, so orthographic view doesn't carry into another screen.
    internal static void LeftEditor() => Ui.Guard("Orthographic view", () =>
    {
        if (ortho) ToggleOrtho();
        RestoreCloseZoom();
        RestoreHalfGrid();
        Hotkeys.LeftEditor();
        Ui.LeftEditor();
        vehicleBoxAt = -1;
        vehicleBoxOwner = IntPtr.Zero;
        vehicleBox = default;
        foreach (var fill in fills) if (fill != null) UnityEngine.Object.Destroy(fill);
        fills.Clear(); fullbrightShadows = false;
        if (shadowless.Count > 0) ToggleShadows();
        if (flashlight != null) UnityEngine.Object.Destroy(flashlight);
        flashlight = null; flashLight = null; flashHd = null;
        orbit = null;
    });

    static void ToggleOrtho()
    {
        ortho = !ortho;
        arrowDraws = 0;
        measuresLogged = false;
        if (!ortho) { held = null; PutCameraBack(); }
        Plugin.ModLog.LogInfo($"Orthographic view {(ortho ? "on" : "off")}");
    }

    /// Perspective again, where the game put the camera, with its own far cut.
    static void PutCameraBack()
    {
        var cam = orthoOwner;
        if (cam != null)
        {
            cam.orthographic = originalOrthographic;
            cam.orthographicSize = originalOrthoSize;
            if (orbit != null) cam.transform.SetPositionAndRotation(orbit.AppliedPosition, orbit.AppliedRotation);
        }
        Backdrop(cam, plain: false);
        if (farBefore is { } far && cam != null) cam.farClipPlane = far;
        farBefore = null;
        Ground(cam, default, hide: false);
        Floor(hide: false);
        Fog(off: false);
        ArrowObserversBack();
        orthoOwner = null;
    }

    // The spawn pad and ground under the vehicle, hidden in orthographic view (the vehicle alone, as a drawing shows it).
    static readonly List<Renderer> floorHidden = new();
    static readonly List<Terrain> terrainHidden = new();
    static float floorAt = -10;
    static bool floorLogged;

    internal static void Floor(bool hide)
    {
        if (!hide)
        {
            foreach (var r in floorHidden) if (r != null) r.enabled = true;
            foreach (var t in terrainHidden) if (t != null) t.enabled = true;
            floorHidden.Clear();
            terrainHidden.Clear();
            floorAt = -10;
            floorLogged = false;
            return;
        }
        // Looked at again every 2 s: a reload brings the pad back.
        if (Time.unscaledTime - floorAt < 2) return;
        floorAt = Time.unscaledTime;
        var own = new HashSet<IntPtr>();
        Bounds? vehicle = null;
        foreach (var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
            foreach (var r in part.GetComponentsInChildren<Renderer>())
            {
                own.Add(r.Pointer);
                // Only what's seen sets the vehicle's bottom: an unseen renderer lower down left the pad's blocks showing.
                if (Drawn(r)) vehicle = Grow(vehicle, r.bounds);
            }
        if (vehicle is not { } box) return;
        // Anything not of the vehicle, under its footprint and low: lying under it (the ground, the pad's deck) or standing
        // round its bottom (the pad's blocks and rails beside the tracks). Not the editor's own handles.
        int before = floorHidden.Count;
        var left = new List<string>();
        foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Renderer>()))
        {
            if (o.TryCast<Renderer>() is not { } r || !r.enabled || own.Contains(r.Pointer) || r.GetComponentInParent<Sprocket.Vehicles.VehicleObject>() != null) continue;
            var b = r.bounds;
            if (!(b.min.x < box.max.x && b.max.x > box.min.x && b.min.z < box.max.z && b.max.z > box.min.z) || b.max.y < box.min.y - 5 || b.min.y > box.min.y + 1) continue;
            bool handle = r.gameObject.layer == 5; // UI
            for (var t = r.transform; t != null && !handle; t = t.parent) handle = t.name.IndexOf("gizmo", StringComparison.OrdinalIgnoreCase) >= 0 || t.name.IndexOf("handle", StringComparison.OrdinalIgnoreCase) >= 0;
            bool low = b.max.y <= box.min.y + 0.25f || (b.min.y <= box.min.y + 0.3f && b.max.y <= box.min.y + 0.4f * box.size.y);
            if (low && !handle)
            {
                r.enabled = false;
                floorHidden.Add(r);
            }
            else if (left.Count < 10) left.Add($"{r.gameObject.name} ({b.min.y - box.min.y:0.00} to {b.max.y - box.min.y:0.00} m above the vehicle's bottom{(handle ? ", a handle" : "")})");
        }
        foreach (var t in Terrain.activeTerrains)
            if (t != null && t.enabled) { t.enabled = false; terrainHidden.Add(t); }
        if (floorHidden.Count > before || !floorLogged)
        {
            floorLogged = true;
            Plugin.ModLog.LogInfo($"Orthographic view: hid the floor and pad: {string.Join(", ", floorHidden.Where(r => r != null).Select(r => r.gameObject.name).Distinct())}; terrains {terrainHidden.Count}" +
                                  (left.Count > 0 ? $"; left low under the vehicle: {string.Join(", ", left)}" : ""));
        }
    }

    // The ground, hidden while looking from below: by its layer, or by its own renderers if a vehicle part shares the
    // layer (then hiding the layer would hide the part too).
    static int? groundLayer;
    static int maskBefore;
    static bool groundTried;
    static readonly List<Renderer> groundHidden = new();

    static void Ground(Camera? cam, Vector3 at, bool hide)
    {
        if (!hide)
        {
            if (groundLayer != null && cam != null) cam.cullingMask = maskBefore;
            groundLayer = null;
            foreach (var r in groundHidden) if (r != null) r.enabled = true;
            groundHidden.Clear();
            groundTried = false;
            return;
        }
        if (cam == null || groundTried) return;
        groundTried = true;
        // The first thing under the point in view that isn't part of the vehicle.
        Collider? ground = null;
        foreach (var hit in Physics.RaycastAll(at + Vector3.up * 50, Vector3.down, 1000f).OrderBy(h => h.distance))
            if (hit.collider != null && hit.collider.GetComponentInParent<Sprocket.Vehicles.VehicleObject>() == null) { ground = hit.collider; break; }
        if (ground == null) { Plugin.ModLog.LogInfo("View from below: found no ground under the vehicle"); return; }
        int layer = ground.gameObject.layer;
        bool shared = DesignEditor.Instance?.AllParts().Any(p => p.GetComponentsInChildren<Renderer>().Any(r => r.gameObject.layer == layer)) == true;
        if (!shared)
        {
            maskBefore = cam.cullingMask;
            cam.cullingMask &= ~(1 << layer);
            groundLayer = layer;
        }
        else
            foreach (var r in ground.GetComponentsInChildren<Renderer>().Concat(ground.GetComponentsInParent<Renderer>()))
                if (r.enabled) { r.enabled = false; groundHidden.Add(r); }
        Plugin.ModLog.LogInfo($"View from below: hiding the ground '{ground.gameObject.name}' " +
                              (shared ? $"({groundHidden.Count} of its renderers; a vehicle part shares its layer {layer})" : $"(layer {layer})"));
    }

    /// Each time the game's orbit camera places itself: in orthographic view the view is the size a perspective view
    /// shows at the orbit distance (zooming still works; the ground doesn't count), and, with "see the whole vehicle",
    /// the camera steps back along its view so its near cut never slices into the vehicle. An orthographic view looks
    /// the same from any distance. Set from the game's own position every time, so nothing adds up.
    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.OrbitalMovementController), nameof(Sprocket.OrbitalMovementController.ApplyInputs))]
    static void OrthoCamera(Sprocket.OrbitalMovementController __instance) => Ui.Guard("Orthographic view", () =>
    {
        if (DrawingSheet.Capturing || PhotoShot.Capturing) return;
        var cam = Camera.main;
        if (orthoOwner is not null && (orthoOwner == null || orthoOwner.Pointer != cam?.Pointer)) PutCameraBack();
        orbit = __instance;
        CloseZoom(__instance);
        if (!ortho) return;
        if (cam == null) return;
        if (orthoOwner == null)
        {
            orthoOwner = cam;
            originalOrthographic = cam.orthographic;
            originalOrthoSize = cam.orthographicSize;
        }
        if (!orthoLogged)
        {
            orthoLogged = true;
            Plugin.ModLog.LogInfo($"Orthographic view: orbit distance {__instance.Distance:0.00} m, camera {Vector3.Distance(cam.transform.position, __instance.AppliedPosition):0.000} m from where the orbit put it");
        }
        cam.orthographic = true;
        Floor(hide: true);
        Fog(off: true);
        // Sized by the zoom the player set (the target distance), not the distance the orbit is at right now: that one
        // moves a little every frame as the camera keeps out of parts, and would shake the view.
        cam.orthographicSize = Math.Max(0.005f, __instance.TargetDistance * MathF.Tan(cam.fieldOfView * MathF.PI / 360) / orthoZoom);
        float back = orthoWhole ? PullBack : 0;
        // Where the orbit is heading: it only changes when the player turns the camera, not while it's still easing.
        var aim = __instance.TargetRotation * Vector3.forward;
        if (held != null && Vector3.Angle(aim, heldAim) > 2) held = null; // the player orbited: back to snapping
        if (orthoLock || held != null)
        {
            // A view picked with Numpad 1 / 3 / 7, else the straight view nearest to where the orbit is heading; at the
            // point the orbit is heading to look at (steady: no easing or shake in it), so panning still works.
            var at = __instance.TargetPosition + aim * __instance.TargetDistance;
            var dir = held ?? Straight.OrderByDescending(d => Vector3.Dot(d, aim)).First();
            shown = dir;
            // Looking straight down or up, the vehicle's front is at the top of the screen.
            cam.transform.rotation = Quaternion.LookRotation(dir, Mathf.Abs(dir.y) > 0.5f ? Vector3.forward : Vector3.up);
            cam.transform.position = at - dir * (__instance.TargetDistance + back);
            Ground(cam, at, hide: dir.y > 0.5f); // from below, the ground is in the way
        }
        // Where the game puts the camera, set whole every time (the game doesn't always set it again, so a step taken
        // from wherever the camera is would add up and fly away), then straight back along that same view: an
        // orthographic picture doesn't change along its own view, so this can't shake either.
        else
        {
            var rotation = __instance.AppliedRotation;
            cam.transform.SetPositionAndRotation(__instance.AppliedPosition - rotation * Vector3.forward * back, rotation);
            Ground(cam, default, hide: false);
        }
        if (back > 0)
        {
            farBefore ??= cam.farClipPlane;
            cam.farClipPlane = Math.Max(farBefore.Value, __instance.Distance + back + 1000);
        }
        Backdrop(cam, plain: backdrop > 0);
    });

    // The move, turn and scale arrows are sized by how far their observer (the camera) is: in orthographic view that's
    // 100 m back, and they came out huge. They get a stand-in observer where a normal view at this zoom would stand.
    static GameObject? arrowsViewpoint;
    static readonly Dictionary<IntPtr, (Sprocket.Transformations.Gizmos.TransformGizmo Gizmo, Transform Observer)> arrowObservers = new();

    // TransformGizmoHandle.Update passes a fixed 100 m to GetAxis. The whole-view camera is
    // pulled back by 100 m in addition to its orbit distance: rings draw, but the ray stops
    // short of their colliders. Extend only orthographic picking to cover this gizmo's bounds.
    [HarmonyPrefix, HarmonyPatch(typeof(Sprocket.Transformations.Gizmos.TransformGizmo), nameof(Sprocket.Transformations.Gizmos.TransformGizmo.GetAxis))]
    static void ReachOrthoGizmo(Sprocket.Transformations.Gizmos.TransformGizmo __instance, Ray r, ref float maxDistance)
    {
        if (!ortho || Camera.main?.orthographic != true) return;
        try
        {
            var centre = __instance.transform.position;
            float radius = 0;
            var axes = __instance.axes;
            if (axes != null)
                foreach (var axis in axes)
                    if (axis?.Collider is { } collider)
                    {
                        var bounds = collider.bounds;
                        radius = Math.Max(radius, Vector3.Distance(centre, bounds.center) + bounds.extents.magnitude);
                    }
            maxDistance = GizmoPicking.Reach(maxDistance, Vector3.Distance(r.origin, centre), radius, true);
        }
        catch (Exception ex) { Ui.Guard("Orthographic gizmo picking", () => throw ex); }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(Sprocket.Transformations.Gizmos.TransformGizmo), nameof(Sprocket.Transformations.Gizmos.TransformGizmo.GetSingleAxis))]
    static void ReachSingleOrthoGizmo(Sprocket.Transformations.Gizmos.TransformGizmo __instance, Ray r, ref float maxDistance)
        => ReachOrthoGizmo(__instance, r, ref maxDistance);

    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.GizmoRendering.TransformGizmos), nameof(Sprocket.GizmoRendering.TransformGizmos.DrawTransform))]
    static void ArrowSize(float scale, Sprocket.Transformations.Gizmos.ITransformGizmo __result) => Ui.Guard("Orthographic view", () =>
    {
        if (__result?.TryCast<Sprocket.Transformations.Gizmos.TransformGizmo>() is not { } gizmo) return;
        var cam = Camera.main;
        if (ortho && cam != null && cam.orthographic)
        {
            if (arrowsViewpoint == null) arrowsViewpoint = new GameObject("Quality of Life arrows viewpoint");
            float seen = cam.orthographicSize / MathF.Tan(cam.fieldOfView * MathF.PI / 360); // a normal view's distance at this zoom
            var stand = arrowsViewpoint.transform;
            stand.SetPositionAndRotation(gizmo.transform.position - cam.transform.forward * seen, cam.transform.rotation);
            if (gizmo.observer?.Pointer != stand.Pointer) { arrowObservers[gizmo.Pointer] = (gizmo, gizmo.observer!); gizmo.observer = stand; }
            // Drawing them sizes them by the camera itself (so they flickered big): their size from the stand-in instead.
            gizmo.transform.localScale = gizmo.ScreenScale;
        }
        else if (arrowObservers.Remove(gizmo.Pointer, out var was)) gizmo.observer = was.Observer;
        if (++arrowDraws != 30) return;
        var o = gizmo.observer;
        Plugin.ModLog.LogInfo($"Transform arrows ({(ortho ? "orthographic" : "normal")} view): the game's size {scale:0.000}, Scale {gizmo.Scale:0.000} x {gizmo.ScaleMultiplier:0.000}, " +
                              $"screen scale {gizmo.ScreenScale.x:0.000}, drawn at {gizmo.transform.lossyScale.x:0.000}; observer '{o?.name}' " +
                              $"{(o != null ? Vector3.Distance(o.position, gizmo.transform.position) : -1):0.0} m away, camera {(cam != null ? Vector3.Distance(cam.transform.position, gizmo.transform.position) : -1):0.0} m");
    });

    /// Out of orthographic view: every arrow set's own observer back.
    static void ArrowObserversBack()
    {
        foreach (var (gizmo, observer) in arrowObservers.Values) if (gizmo != null) gizmo.observer = observer;
        arrowObservers.Clear();
        if (arrowsViewpoint != null) UnityEngine.Object.Destroy(arrowsViewpoint);
        arrowsViewpoint = null;
    }

    // Orthographic backdrop: the scene as it is, or plain: no sky (one colour behind) and no map (the camera draws only
    // the depth the vehicle fills, so walls, hills and the map's edge in front or behind don't show).
    static int backdrop = 1; // index into BackdropNames
    static readonly string[] BackdropNames = { "Backdrop: scene", "Backdrop: grey", "Backdrop: white", "Backdrop: black" };
    static readonly Color[] BackdropColours = { default, new(0.32f, 0.33f, 0.35f), Color.white, Color.black };
    static (UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData Hd, UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.ClearColorMode Mode, Color Colour)? clearBefore;
    static float? orthoNearBefore;
    static (Bounds? All, Bounds? Body) vehicleBox;
    static IntPtr vehicleBoxOwner;
    static float vehicleBoxAt = -1;

    static void Backdrop(Camera? cam, bool plain)
    {
        if (!plain)
        {
            if (clearBefore is { } c && c.Hd != null) { c.Hd.clearColorMode = c.Mode; c.Hd.backgroundColorHDR = c.Colour; }
            clearBefore = null;
            if (orthoNearBefore is { } near && cam != null) cam.nearClipPlane = near;
            orthoNearBefore = null;
            if (!orthoWhole && farBefore is { } far && cam != null) cam.farClipPlane = far;
            return;
        }
        if (cam == null) return;
        if (cam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>() is { } hd)
        {
            clearBefore ??= (hd, hd.clearColorMode, hd.backgroundColorHDR);
            hd.clearColorMode = UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.ClearColorMode.Color;
            hd.backgroundColorHDR = BackdropColours[backdrop];
        }
        if (VehicleBounds() is not { } box) return;
        float nearest = float.MaxValue, furthest = float.MinValue;
        var position = cam.transform.position;
        var forward = cam.transform.forward;
        for (int i = 0; i < 8; i++)
        {
            float depth = Vector3.Dot(new Vector3((i & 1) == 0 ? box.min.x : box.max.x, (i & 2) == 0 ? box.min.y : box.max.y,
                (i & 4) == 0 ? box.min.z : box.max.z) - position, forward);
            nearest = Math.Min(nearest, depth); furthest = Math.Max(furthest, depth);
        }
        orthoNearBefore ??= cam.nearClipPlane;
        farBefore ??= cam.farClipPlane;
        cam.nearClipPlane = Math.Max(0.01f, nearest - 0.05f);
        cam.farClipPlane = Math.Max(cam.nearClipPlane + 0.1f, furthest + 0.05f);
    }

    // The game's height fog, off in orthographic view: the camera stands 100 m back (a haze over the vehicle), and below
    // a height the fog paints the backdrop another colour. Off for the drawing sheet too.
    static readonly List<UnityEngine.Rendering.VolumeComponent> fogsOff = new();
    internal static bool FogOff { get; private set; }

    internal static void Fog(bool off)
    {
        if (off == FogOff) return;
        FogOff = off;
        if (!off)
        {
            foreach (var f in fogsOff) if (f != null) f.active = true;
            fogsOff.Clear();
            return;
        }
        foreach (var volume in UnityEngine.Object.FindObjectsOfType<UnityEngine.Rendering.Volume>())
        {
            var parts = (volume.HasInstantiatedProfile() ? volume.profile : volume.sharedProfile)?.components;
            if (parts != null)
                for (int k = 0; k < parts.Count; k++)
                    if (parts[k] != null && parts[k].active && parts[k].TryCast<UnityEngine.Rendering.HighDefinition.Fog>() != null) { parts[k].active = false; fogsOff.Add(parts[k]); }
        }
    }

    // ---------- measurements (orthographic view) ----------

    static bool orthoMeasure = true, measuresLogged;
    static GUIStyle? inkStyle, measureStyle;
    static readonly Color Ink = new(1f, 0.8f, 0.15f), Shade = new(0, 0, 0, 0.7f);
    const float MeasureGap = 40; // pixels from the vehicle to its dimension lines

    /// In a straight orthographic view: the vehicle's overall size across the screen (under it) and up the screen (to its
    /// right), drawn as a drawing's dimensions, to the centimetre. Antennas left out. From the editor's OnGUI.
    internal static void DrawMeasures()
    {
        if (!ortho || !orthoMeasure || !(orthoLock || held != null)) return;
        var cam = Camera.main;
        if (cam == null || !cam.orthographic || BodyBounds() is not { } box) return;
        if (!measuresLogged) { measuresLogged = true; LogMeasuredBox(box); }
        float left = float.MaxValue, right = float.MinValue, top = float.MaxValue, bottom = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            var s = cam.WorldToScreenPoint(new Vector3((i & 1) == 0 ? box.min.x : box.max.x, (i & 2) == 0 ? box.min.y : box.max.y, (i & 4) == 0 ? box.min.z : box.max.z));
            float y = Screen.height - s.y; // the GUI's y runs down the screen
            left = Math.Min(left, s.x); right = Math.Max(right, s.x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        float across = Math.Abs(Vector3.Dot(box.size, cam.transform.right)), up = Math.Abs(Vector3.Dot(box.size, cam.transform.up));
        float under = bottom + MeasureGap, beside = right + MeasureGap;
        var bars = new[]
        {
            new Rect(left, under - 1, right - left, 2), new Rect(left - 1, under - 10, 2, 20), new Rect(right - 1, under - 10, 2, 20),
            new Rect(beside - 1, top, 2, bottom - top), new Rect(beside - 10, top - 1, 20, 2), new Rect(beside - 10, bottom - 1, 20, 2),
        };
        inkStyle ??= new GUIStyle { normal = { background = Texture2D.whiteTexture } };
        measureStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        var was = GUI.color;
        // All the dark edges first, then the lines over them (so where two lines meet, neither's edge crosses the other).
        GUI.color = Shade;
        foreach (var r in bars) GUI.Box(new Rect(r.x - 1, r.y - 1, r.width + 2, r.height + 2), "", inkStyle);
        GUI.color = Ink;
        foreach (var r in bars) GUI.Box(r, "", inkStyle);
        MeasureText(new Rect((left + right) / 2 - 100, under + 10, 200, 26), $"{across:0.00} m", TextAnchor.UpperCenter);
        MeasureText(new Rect(beside + 16, (top + bottom) / 2 - 13, 200, 26), $"{up:0.00} m", TextAnchor.MiddleLeft);
        GUI.color = was;
    }

    static void MeasureText(Rect at, string text, TextAnchor anchor)
    {
        measureStyle!.alignment = anchor;
        GUI.color = Shade;
        GUI.Box(new Rect(at.x + 2, at.y + 2, at.width, at.height), text, measureStyle);
        GUI.color = Ink;
        GUI.Box(at, text, measureStyle);
    }

    // ---------- zooming in close (small parts) ----------

    static bool closeZoom = true;
    const float ClosestZoom = 0.05f; // metres from what the camera orbits
    static float? minBefore, nearBefore;
    static Sprocket.OrbitalMovementController? zoomOwner;
    static Camera? zoomCamera;

    static void RestoreCloseZoom()
    {
        if (zoomOwner != null && minBefore is { } min) zoomOwner.minDistance = min;
        if (zoomCamera != null && nearBefore is { } near)
        {
            if (ortho && orthoNearBefore != null) orthoNearBefore = near;
            else zoomCamera.nearClipPlane = near;
        }
        zoomOwner = null; zoomCamera = null;
        minBefore = null; nearBefore = null;
    }

    /// The game's orbit camera stops well short of small parts; this lets it come within a few centimetres, and the
    /// camera's near cut follows it in (only while close, so far views keep their depth precision).
    static void CloseZoom(Sprocket.OrbitalMovementController o)
    {
        var cam = Camera.main;
        if ((zoomOwner is not null && (zoomOwner == null || zoomOwner.Pointer != o.Pointer)) ||
            (zoomCamera is not null && (zoomCamera == null || zoomCamera.Pointer != cam?.Pointer))) RestoreCloseZoom();
        if (!closeZoom)
        {
            RestoreCloseZoom();
            return;
        }
        if (minBefore == null)
        {
            minBefore = o.minDistance;
            zoomOwner = o;
            Plugin.ModLog.LogInfo($"Zoom in close: the game's closest zoom was {o.minDistance:0.00} m, now {ClosestZoom:0.00} m");
        }
        o.minDistance = Math.Min(minBefore.Value, ClosestZoom);
        if (cam == null || ortho) return; // orthographic view sets its own
        zoomCamera = cam;
        nearBefore ??= cam.nearClipPlane;
        cam.nearClipPlane = Math.Clamp(o.Distance * 0.2f, Math.Min(0.005f, nearBefore.Value), nearBefore.Value);
    }

    // The straight views, as the direction the camera looks: from the front, back, right, left, and from above.
    // (From below only with Ctrl+Numpad 7: the game's orbit camera can't go under the ground.)
    static readonly Vector3[] Straight = { Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.down };

    // A view picked with Numpad 1 / 3 / 7, held until the player turns the camera (the orbit's own heading then).
    static Vector3? held;
    static Vector3 shown = Vector3.back; // the straight view on screen
    static Vector3 heldAim;

    /// Numpad 1 / 3 / 7 (Ctrl: the opposite side): orthographic view from the front, the side or the top. The game's
    /// orbit camera isn't turned (it would ease back to its own heading); the view is held until you orbit.
    static void LookFrom(Vector3 dir)
    {
        if (!ortho) ToggleOrtho();
        held = dir;
        Plugin.ModLog.LogInfo($"Orthographic view held looking {dir} (Ctrl {(Keyboard.current?.ctrlKey.isPressed == true ? "held" : "not held")})");
        heldAim = orbit != null ? orbit.TargetRotation * Vector3.forward : Vector3.forward;
    }

    // ---------- proportional editing ----------

    /// One move, scale or rotate: the points it moves, the points near them that follow, and how much.
    sealed class Session
    {
        public EditMesh Mesh = null!;
        public readonly List<Vertex> Movers = new(), Near = new();
        public readonly List<Num> MoverFrom = new(), NearFrom = new();
        public readonly List<(int[] Movers, float[] Weights)> Follow = new();
        public Num[] Delta = Array.Empty<Num>();
        public Num[]? Final;
    }
    // By the game's move object. Holding the object keeps its address from being reused by a later move while its
    // session is kept (for undo and redo); the oldest go past a limit.
    static readonly Dictionary<IntPtr, (Transformation Op, Session? S)> sessions = new();
    static readonly Queue<IntPtr> sessionOrder = new();
    const int KeptSessions = 256;

    static Session? SessionOf(IntPtr op) => sessions.TryGetValue(op, out var s) ? s.S : null;

    static void Begin(MeshTransformation t, Transformation op)
    {
        if (sessions.ContainsKey(op.Pointer)) return;
        sessions[op.Pointer] = (op, null);
        sessionOrder.Enqueue(op.Pointer);
        while (sessionOrder.Count > KeptSessions) sessions.Remove(sessionOrder.Dequeue());
        if (!proportional) return;
        var mesh = t.mesh;
        var all = new List<Vertex>();
        var pos = new List<Num>();
        var list = mesh.vertices;
        for (int i = 0; i < list.Count; i++) { all.Add(list[i]); pos.Add(HoleQuality.ToNum(list[i].position)); }
        var moving = Enumerable.Range(0, all.Count).Where(i => all[i].HasFlag(ElementFlags.Selected)).ToHashSet();
        if (Hotkeys.Current?.meshEditor.Symmetry == true) moving.UnionWith(MeshPlans.Twins(pos, moving.ToList(), Tolerance).Values); // the game moves those too
        var s = new Session { Mesh = mesh };
        var slot = new Dictionary<int, int>();
        foreach (int m in moving) { slot[m] = s.Movers.Count; s.Movers.Add(all[m]); s.MoverFrom.Add(pos[m]); }
        s.Delta = new Num[s.Movers.Count];
        foreach (var (v, (movers, weights)) in MeshPlans.Falloff(pos, moving, radiusMm / 1000))
        {
            s.Near.Add(all[v]);
            s.NearFrom.Add(pos[v]);
            s.Follow.Add((movers.Select(m => slot[m]).ToArray(), weights));
        }
        sessions[op.Pointer] = (op, s);
        Plugin.ModLog.LogInfo($"Proportional editing: {s.Movers.Count} points moving, {s.Near.Count} following within {radiusMm:0} mm");
    }

    static void Follow(Transformation op)
    {
        if (SessionOf(op.Pointer) is not { } s || s.Near.Count == 0) return;
        var delta = s.Delta;
        for (int i = 0; i < s.Movers.Count; i++) delta[i] = HoleQuality.ToNum(s.Movers[i].position) - s.MoverFrom[i];
        s.Final ??= new Num[s.Near.Count];
        for (int i = 0; i < s.Near.Count; i++)
        {
            var (movers, weights) = s.Follow[i];
            var d = Num.Zero;
            for (int j = 0; j < movers.Length; j++) d += weights[j] * delta[movers[j]];
            var at = s.NearFrom[i] + d;
            s.Final[i] = at;
            s.Near[i].position = new Vector3(at.X, at.Y, at.Z);
        }
        s.Mesh.MarkDirty(MeshDirtyFlags.Geometry);
    }

    static void Put(Session? s, bool final)
    {
        if (s == null || final && s.Final == null) return;
        for (int i = 0; i < s.Near.Count; i++)
        {
            var at = final ? s.Final![i] : s.NearFrom[i];
            s.Near[i].position = new Vector3(at.X, at.Y, at.Z);
        }
        s.Mesh.MarkDirty(MeshDirtyFlags.Geometry);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyTranslation))]
    static void MoveStart(MeshTransformation __instance, Transformation op) => Ui.Guard("Proportional editing", () => Begin(__instance, op));
    [HarmonyPrefix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyScale))]
    static void ScaleStart(MeshTransformation __instance, Transformation op) => Ui.Guard("Proportional editing", () => Begin(__instance, op));
    [HarmonyPrefix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyRotation))]
    static void RotateStart(MeshTransformation __instance, Transformation op) => Ui.Guard("Proportional editing", () => Begin(__instance, op));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyTranslation))]
    static void Moved(Transformation op) => Ui.Guard("Proportional editing", () => Follow(op));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyScale))]
    static void Scaled(Transformation op) => Ui.Guard("Proportional editing", () => Follow(op));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.ApplyRotation))]
    static void Rotated(Transformation op) => Ui.Guard("Proportional editing", () => Follow(op));

    // Cancelled, undone, redone: the followers go back, or forward again, with the moved points.
    [HarmonyPostfix, HarmonyPatch(typeof(MeshTransformation), nameof(MeshTransformation.Restore))]
    static void Cancelled(Transformation op) => Ui.Guard("Proportional editing", () => Put(SessionOf(op.Pointer), false));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Revert))]
    static void Undone(MeshGeometryEditOp __instance) => Ui.Guard("Proportional editing", () => Put(SessionOf(__instance.transformation?.Pointer ?? IntPtr.Zero), false));
    [HarmonyPostfix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Execute))]
    static void Redone(MeshGeometryEditOp __instance) => Ui.Guard("Proportional editing", () => Put(SessionOf(__instance.transformation?.Pointer ?? IntPtr.Zero), true));

    // ---------- panel ----------

    internal static void Draw(PlateStructureEditor __instance, Panel layout) => Ui.Inspector("Mesh tools", layout, () =>
    {
        var ui = Ui.Drawer(layout);
        if (ui == null || __instance.TryCast<FreeformPlateStructureEditor>() == null) return;
        Ui.Section(layout, "Mesh tools");
        ui.InfoField("Choose Points, Edges or Faces before selecting.\nMesh edits follow Mirror; Ctrl+Z undoes a step.", 2);
        var flattenTip = new Tip("Flatten", "Select three or more points or a face, then choose a mode. Level and axis modes also work with two points. Best-fit makes one flat plane; level makes one height. Sideways keeps one X position; lengthways keeps one Z position. Click the mode to change it. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Button("Flatten (P)", Ui.Callback(() => Flatten(__instance)), flattenTip);
        ui.Button(FlattenNames[(int)flattenMode], Ui.Callback(() => { flattenMode = (MeshPlans.FlattenMode)(((int)flattenMode + 1) % FlattenNames.Length); __instance.RequestRedraw(); }), flattenTip);
        var insetTip = new Tip("Inset", "In Faces mode, select faces and choose a width in millimetres. Inset adds a smaller inner face with a border around it. Adjacent selected faces inset together. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Slider("Inset width (mm)", insetMm, 1, 500, Ui.FloatCallback(v => insetMm = MathF.Round(v)));
        ui.Button("Inset (I)", Ui.Callback(() => Inset(__instance)), insetTip);

        Ui.Section(layout, "Edge rounding");
        ui.InfoField("Edges mode: select edges to round.\nMirror applies; Ctrl+Z undoes the whole edit.", 2);
        var bevelTip = new Tip("Bevel", "In Edges mode, select edges with a face on each side. Width is the distance cut back on each side, in millimetres. Bevel adds one flat strip across each corner. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Slider("Bevel width (mm)", bevelMm, 1, 500, Ui.FloatCallback(v => bevelMm = MathF.Round(v)));
        ui.Button("Bevel (V)", Ui.Callback(() => Bevel(__instance)), bevelTip);
        var smoothTip = new Tip("Smooth Edge", "In Edges mode, select edges with a face on each side. Width sets the distance cut back on each side, in millimetres. More segments make the curve smoother. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Slider("Smooth width (mm)", smoothMm, 1, 500, Ui.FloatCallback(v => smoothMm = MathF.Round(v)));
        ui.Slider("Smooth segments", smoothSegments, 2, 16, Ui.FloatCallback(v => smoothSegments = Math.Clamp((int)MathF.Round(v), 2, 16)));
        ui.Button("Smooth Edge", Ui.Callback(() => SmoothEdge(__instance)), smoothTip);
        var filletTip = new Tip("Fillet", "In Edges mode, select corners between two flat faces. Radius is the size of the circular curve, in millimetres; more segments make it smoother. Reduce the radius if it cannot fit. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Slider("Fillet radius (mm)", filletRadiusMm, 1, 500, Ui.FloatCallback(v => filletRadiusMm = MathF.Round(v)));
        ui.Slider("Fillet segments", filletSegments, 2, 16, Ui.FloatCallback(v => filletSegments = Math.Clamp((int)MathF.Round(v), 2, 16)));
        ui.Button("Fillet", Ui.Callback(() => Fillet(__instance)), filletTip);
        Ui.Section(layout, "Subdivision");
        ui.InfoField("Loop cut: select an edge.\nSplit faces: use Faces mode and select faces.", 2);
        var loopTip = new Tip("Loop cut", "In Edges mode, select an edge. Loop cut runs through adjoining four-sided faces and adds one cut halfway across them. It stops at an open border or a triangle. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Button("Loop cut (T)", Ui.Callback(() => LoopCut(__instance)), loopTip);
        var splitTip = new Tip("Split faces", "In Faces mode, select the faces to divide. Sections sets how many equal strips to create. Direction A/B changes the cut direction on four-sided faces. The strips stay inside the selected faces; neighbouring borders stay joined using triangles and quads. Turn Mirror off to affect only one side; Ctrl+Z undoes the edit.");
        ui.Slider("Split sections", splitSections, 2, 16, Ui.FloatCallback(v => splitSections = Math.Clamp((int)MathF.Round(v), 2, 16)));
        ui.Button(splitOtherDirection ? "Split direction: B" : "Split direction: A", Ui.Callback(() => { splitOtherDirection = !splitOtherDirection; __instance.RequestRedraw(); }), splitTip);
        ui.Button("Split selected faces", Ui.Callback(() => SplitEdges(__instance)), splitTip);
        var betweenTip = new Tip("Select between splits", "Selects all faces created inside the latest split on this structure. Split faces first. If that split was undone or its faces changed, redo it or split again before using this button.");
        ui.Button("Select between splits", Ui.Callback(() => SelectBetweenSplits(__instance)), betweenTip);

        Ui.Section(layout, "Selection and movement");
        ui.InfoField("Select linked flat starts from a selected face.\nProportional editing also moves nearby points.", 2);
        var flatTip = new Tip("Select linked flat", "In Faces mode, select a starting face. Adds connected faces within the angle below. A smaller angle follows flatter surfaces; a larger angle also follows gentle bends. Mirror applies.");
        ui.Slider("Flat angle (°)", flatAngle, 0.5f, 30, Ui.FloatCallback(v => flatAngle = MathF.Round(v * 2) / 2));
        ui.Button("Select linked flat (U)", Ui.Callback(() => SelectFlat(__instance)), flatTip);
        ui.ToggleField("Proportional (O)", proportional, Ui.BoolCallback(v => proportional = v),
            "When moving, scaling or rotating selected points, nearby points follow. Influence fades to zero at the radius below. Mirror applies; Ctrl+Z undoes the move.");
        ui.Slider("Influence radius (mm)", radiusMm, 10, 5000, Ui.FloatCallback(v => radiusMm = MathF.Round(v)));
        ui.ToggleField("0.5 mm grid", halfGrid, Ui.BoolCallback(v =>
        {
            halfGrid = v;
            if (!v) RestoreHalfGrid();
        }), "Hold the snap key (normally Ctrl) while moving to snap to a 0.5 mm grid.");
        ui.InfoField("Rotation snap: 0 uses the game's setting.", 1);
        ui.Slider("Rotation snap (°)", Plugin.RotationSnap?.Value ?? 0, 0, 90, Ui.FloatCallback(v =>
        {
            if (Plugin.RotationSnap != null) Plugin.RotationSnap.Value = MathF.Round(v * 4) / 4; // quarter degrees: 3.75° is a 96-sided circle
        }));

        Ui.Section(layout, "Bridge and circle");
        ui.InfoField("Bridge: select two open edge chains in Edges mode.\nCircle: select points around a loop.", 2);
        var bridgeTip = new Tip("Bridge", "In Edges mode, select two open edge chains or two loops with the same number of points. Bridge joins them with faces. Cuts adds rows between the chains. Smoothness 0 gives a straight join; 100 follows the adjoining surfaces into a curve. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Slider("Bridge cuts", bridgeCuts, 0, 32, Ui.FloatCallback(v => bridgeCuts = (int)Math.Round(v)));
        ui.Slider("Bridge smoothness (%)", bridgeSmooth, 0, 200, Ui.FloatCallback(v => bridgeSmooth = MathF.Round(v)));
        ui.Button("Bridge selected edges", Ui.Callback(() => Bridge(__instance)), bridgeTip);
        var circleTip = new Tip("Circle", "In Points mode, select at least three points around a loop. Circle spaces them evenly on a flat circle around their centre. To make a smoother circle, add more points with Loop cut first. Mirror applies; Ctrl+Z undoes the edit.");
        ui.Button("Circle selected points", Ui.Callback(() => Circle(__instance)), circleTip);

        Ui.Section(layout, "Mirror fixes");
        ui.InfoField("Fix mirror aligns nearly matching points.\nPoints with no matching partner are selected.", 2);
        var fixTip = new Tip("Fix mirror", "Points nearly each other's mirror image (within the distance below) are made exactly so, and points " +
            "that near the middle go onto it, so the editor's Mirror moves them together again. Selected points only, or the whole shape if none are selected. " +
            "Points left with no mirror image are selected afterwards: the two sides differ there (merged, split or filled on one side only).");
        ui.Slider("Pair distance (mm)", mirrorMm, 0.5f, 50, Ui.FloatCallback(v => mirrorMm = MathF.Round(v * 2) / 2));
        ui.Button(KeepNames[(int)mirrorKeep], Ui.Callback(() => { mirrorKeep = (MeshPlans.MirrorKeep)(((int)mirrorKeep + 1) % KeepNames.Length); __instance.RequestRedraw(); }), fixTip);
        ui.Button("Fix mirror", Ui.Callback(() => FixMirror(__instance)), fixTip);
        ui.ToggleField("Mirror point merges (M)", Plugin.MirrorMerge?.Value ?? true, Ui.BoolCallback(v => { if (Plugin.MirrorMerge != null) Plugin.MirrorMerge.Value = v; }),
            "With Mirror on, the game's Merge (M) merges the mirrored points on the other side too, in the same step (Ctrl+Z undoes both).");

        Ui.Section(layout, "View and lighting");
        ui.ToggleField("Part mass markers", MassMarkers.PartMarkersShown, Ui.BoolCallback(v =>
        {
            MassMarkers.SetPartMarkersShown(v);
            __instance.RequestRedraw();
        }), "Shows each part's blue centre-of-mass diamond when COM is enabled in the bottom-right view filters. Turn this off to keep only the vehicle mass marker. Turning COM off hides both kinds.");
        if (MassMarkers.PartMarkersShown && !MassMarkers.MasterShown)
            ui.InfoField("Part mass markers are hidden while COM is off in the view filters.", 1);
        ui.InfoField($"{Keybinds.Shown("ortho")}: orthographic view.\n{Keybinds.Shown("shadows")} shadows, {Keybinds.Shown("flashlight")} flashlight, {Keybinds.Shown("fullbright")} fullbright.", 2);
        ui.ToggleField("Zoom in close", closeZoom, Ui.BoolCallback(v => closeZoom = v),
            "Lets the camera move close to small parts for detailed editing.");
        ui.ToggleField("Ortho: straight views", orthoLock, Ui.BoolCallback(v => orthoLock = v),
            $"Orthographic view snaps to front, back, sides or top (orbiting flips between them). {Keybinds.Shown("front")} / {Keybinds.Shown("side")} / {Keybinds.Shown("top")}: front, side, top; " +
            "with Ctrl, back and the other side. Off: orbit freely.");
        ui.Slider("Ortho zoom (%)", orthoZoom * 100, 10, 1000, Ui.FloatCallback(v => orthoZoom = MathF.Round(v) / 100));
        var backTip = new Tip("Orthographic backdrop", "Click to cycle backgrounds. Scene keeps the sky and map; grey, white and black show only the vehicle on a plain background. Applies in orthographic view.");
        ui.Button(BackdropNames[backdrop], Ui.Callback(() => { backdrop = (backdrop + 1) % BackdropNames.Length; __instance.RequestRedraw(); }), backTip);
        ui.ToggleField("Ortho: whole view", orthoWhole, Ui.BoolCallback(v =>
        {
            orthoWhole = v;
            if (!v && ortho) { var cam = Camera.main; if (cam != null && orbit != null) cam.transform.position = orbit.AppliedPosition; }
        }), $"Orthographic view ({Keybinds.Shown("ortho")}): the camera steps back so it never cuts into the vehicle when you zoom in close. " +
            "Off: it stays where the game puts it, and zooming in close shows the inside.");
        ui.ToggleField("Ortho: measurements", orthoMeasure, Ui.BoolCallback(v => orthoMeasure = v),
            "Orthographic view, looking straight from the front, back, side or top: the vehicle's overall size across the screen " +
            "(under it) and up the screen (beside it), to the centimetre. Antennas aren't counted.");
        ui.InfoField("Light strength is relative to the scene's sunlight.", 1);
        ui.Slider("Flashlight strength (%)", Plugin.FlashlightPercent?.Value ?? 80, 5, 300, Ui.FloatCallback(v => { if (Plugin.FlashlightPercent != null) Plugin.FlashlightPercent.Value = MathF.Round(v); }));
        ui.Slider("Fullbright strength (%)", Plugin.FullbrightPercent?.Value ?? 25, 5, 150, Ui.FloatCallback(v =>
        {
            if (Plugin.FullbrightPercent != null) Plugin.FullbrightPercent.Value = MathF.Round(v);
            FillBrightness();
        }));
    });
}

/// Turrets can be copied with Alt and mirrored like other parts: the game's turret ring part says it can't be duplicated
/// or mirrored, and this lets it, in memory, as the game reads its part files (no game file changes). Copying a ring
/// copies everything on it too (turret body, guns, ...), each copy on the copy of its own parent.
[HarmonyPatch]
public static class TurretCopy
{
    [HarmonyPrefix, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.VehicleOperations), nameof(Sprocket.VehicleDesigner.Operations.VehicleOperations.Duplicate))]
    static void WholeTurret(ref Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject> instances,
        out List<(Sprocket.Vehicles.VehicleObject Part, Sprocket.Vehicles.VehicleObject Parent)> __state)
    {
        // Per-call pairs survive nested duplication without retaining old parts between calls.
        var added = new List<(Sprocket.Vehicles.VehicleObject Part, Sprocket.Vehicles.VehicleObject Parent)>();
        __state = added;
        Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>? more = null;
        var given = instances;
        Ui.Guard("Turret copy", () => more = WithEverythingOnRings(given, added));
        if (more != null) instances = more;
        else added.Clear();
    }

    static Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>? WithEverythingOnRings(
        Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject> instances,
        List<(Sprocket.Vehicles.VehicleObject Part, Sprocket.Vehicles.VehicleObject Parent)> added)
    {
        var sources = instances.Select(i => i?.Object).Where(o => o != null).ToList();
        if (!sources.Any(o => o!.GUID == Conversion.RingGuid)) return null;
        var have = sources.Select(o => o!.Pointer).ToHashSet();
        void Take(Sprocket.Vehicles.VehicleObject parent)
        {
            var children = parent.GetComponent<Sprocket.Vehicles.VehicleTransform>()?.Children;
            if (children == null) return;
            for (int i = 0; i < children.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Sprocket.Vehicles.VehicleTransform>>().Count; i++)
                if (children[i]?.VehicleObject is { } child && have.Add(child.Pointer)) { added.Add((child, parent)); Take(child); }
        }
        foreach (var ring in sources.Where(o => o!.GUID == Conversion.RingGuid).ToList()) Take(ring!);
        if (added.Count == 0) return null;
        Plugin.ModLog.LogInfo($"Turret copy: copying {added.Count} parts on the ring too");
        return new Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>(instances.Concat(added.Select(a => a.Part.GetReference())).ToArray());
    }

    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.VehicleOperations), nameof(Sprocket.VehicleDesigner.Operations.VehicleOperations.Duplicate))]
    static void Reattach(Sprocket.VehicleDesigner.Operations.VehicleOperations __instance, Sprocket.Vehicles.Operations.Duplicate __result, int groupID,
        List<(Sprocket.Vehicles.VehicleObject Part, Sprocket.Vehicles.VehicleObject Parent)>? __state) => Ui.Guard("Turret copy", () =>
    {
        if (__state == null) return;
        try
        {
            if (__state.Count == 0 || __result == null) return;
            int moved = 0, missing = 0;
            foreach (var (part, parent) in __state)
            {
                var copy = __result.GetDupe(part);
                var copyParent = __result.GetDupe(parent);
                if (copy == null || copyParent == null) { missing++; continue; }
                if (copy.GetComponent<Sprocket.Vehicles.VehicleTransform>()?.Parent?.VehicleObject?.Pointer == copyParent.Pointer) continue;
                __instance.SetParent(copyParent.GetReference(), new Il2CppReferenceArray<Sprocket.Vehicles.ISoftVehicleObject>(new[] { copy.GetReference() }), groupID);
                moved++;
            }
            Plugin.ModLog.LogInfo($"Turret copy: {__state.Count} parts copied with the ring; {moved} moved onto the new ring, {__state.Count - moved - missing} already on it" +
                                  (missing > 0 ? $", {missing} copies not found" : ""));
        }
        finally { __state.Clear(); }
    });

    [HarmonyFinalizer, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.VehicleOperations), nameof(Sprocket.VehicleDesigner.Operations.VehicleOperations.Duplicate))]
    static Exception? ClearCopyState(Exception? __exception,
        List<(Sprocket.Vehicles.VehicleObject Part, Sprocket.Vehicles.VehicleObject Parent)>? __state)
    {
        // The original native operation can fail before its postfix runs. Keep its exception unchanged.
        __state?.Clear();
        return __exception;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PartDefinitionIO), nameof(PartDefinitionIO.DeserializePartDefinitionJSON))]
    static void AllowCopy(PartDefinition __result) => Ui.Guard("Turret copy", () =>
    {
        if (__result?.guid != Conversion.RingGuid || __result.transform == null) return;
        __result.transform.options |= PartOptions.Duplication | PartOptions.Mirroring;
        Plugin.ModLog.LogInfo("Turret copy: turret rings can be copied with Alt and mirrored");
    });

    /// What the game does when it makes a turret's mirror twin (for the log): the twin, and what's on each.
    [HarmonyPostfix, HarmonyPatch(typeof(Sprocket.VehicleDesigner.Operations.Place.PlaceOperation), nameof(Sprocket.VehicleDesigner.Operations.Place.PlaceOperation.ApplyDuplicateMirrorState))]
    static void Mirrored(Sprocket.Vehicles.VehicleTransform targetTransform, bool mirrorRequested) => Ui.Guard("Turret mirror", () =>
    {
        var part = targetTransform?.VehicleObject;
        if (part == null || part.GUID != Conversion.RingGuid) return;
        var twin = targetTransform!.Mirror;
        int Count(Sprocket.Vehicles.VehicleTransform? t) => t?.Children?.Cast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<Sprocket.Vehicles.VehicleTransform>>().Count ?? -1;
        string line = $"Turret mirror: ring {(int)part.VUID} mirror requested={mirrorRequested}, mirrored={targetTransform.IsMirrored}, twin ring={(twin?.VehicleObject is { } o ? ((int)o.VUID).ToString() : "none")}, " +
                      $"parts on it {Count(targetTransform)}, on the twin {Count(twin)}";
        if (line != lastMirrorLine) Plugin.ModLog.LogInfo(lastMirrorLine = line); // called every frame while placing: new news only
    });

    static string lastMirrorLine = "";
}
