using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.UI;
using Sprocket.Vehicles.PlateStructures.Design;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

/// Structure panel (hand-made shapes): "Hole quality" sets how many segments and how big the game's own
/// Create Hole tool makes a hole, and every hole's ring is made a true circle inside the face, running the same
/// way round as the face so the filled-in faces aren't inside out. It stays the game's operation, so undo works as normal.
[HarmonyPatch]
public static class HoleQuality
{
    static int segments = 32, sizePercent = 100;
    static bool cardinalPoints = true;

    static ushort? holeThickness; // the holed face's plate thickness, read before the game takes the face away
    static Num? originalSide;
    static PlateStructureEditor? lastEditor;

    sealed class HoleEdit
    {
        internal readonly EditMesh Original;
        internal readonly HoleEdit? Previous;
        internal string? Failure;
        internal HoleEdit(MeshTopologyEditOp topology, HoleEdit? previous)
        { Original = topology.mesh; Previous = previous; }
    }
    static HoleEdit? activeEdit;

    // The native operation removes the old face before it asks FillEdgeLoop to fill
    // around the hole. Keep its undo snapshot so a rejected fill cannot leave half a cut,
    // including when the second, mirrored cut fails. Redo must use the valid snapshot too.
    [HarmonyPrefix, HarmonyPatch(typeof(MeshTopologyEditOp), nameof(MeshTopologyEditOp.Execute))]
    static void BeginHole(MeshTopologyEditOp __instance, out HoleEdit? __state)
    {
        __state = null;
        if (__instance.meshOp?.TryCast<CreateHoleOp>() == null) return;
        lastEditor = Hotkeys.Current;
        activeEdit = __state = new HoleEdit(__instance, activeEdit);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MeshTopologyEditOp), nameof(MeshTopologyEditOp.Execute))]
    static void EndHole(MeshTopologyEditOp __instance, HoleEdit? __state)
    {
        if (__state == null) return;
        try
        {
            if (__state.Failure == null && __instance.final is { } final)
            {
                MeshTools.RepairThickening(final);
                __state.Failure = MeshTools.CloneProblem(final);
            }
        }
        catch (Exception ex) { __state.Failure = ex.Message; }
        finally
        {
            activeEdit = __state.Previous;
            holeThickness = null;
            holeRivets = null;
            faceSide = null;
            originalSide = null;
        }
        if (__state.Failure == null) return;
        __instance.final = __state.Original;
        __instance.Revert(__instance.context);
        __state.Original.MarkDirty(MeshDirtyFlags.All);
        Plugin.ModLog.LogWarning("Create Hole cancelled; original shape restored: " + __state.Failure);
        Ui.Guard("Create Hole", () => lastEditor?.operations.NotifyError("Create Hole: " + __state.Failure + ". Shape unchanged."));
    }

    /// The postfix above doesn't run when Execute throws: the hole's state goes anyway, so the next hole starts clean
    /// (and doesn't nest under this one).
    [HarmonyFinalizer, HarmonyPatch(typeof(MeshTopologyEditOp), nameof(MeshTopologyEditOp.Execute))]
    static Exception? EndHoleAfterError(HoleEdit? __state, Exception? __exception)
    {
        if (__state == null || __exception == null || activeEdit != __state) return __exception;
        activeEdit = __state.Previous;
        holeThickness = null;
        holeRivets = null;
        faceSide = null;
        originalSide = null;
        Plugin.ModLog.LogWarning("Create Hole: the game stopped with an error part way; QoL's hole state was cleared. " + __exception.Message);
        return __exception;
    }

    static void Reject(string reason)
    {
        if (activeEdit != null) activeEdit.Failure ??= reason;
        Plugin.ModLog.LogWarning("Create Hole rejected: " + reason);
        if (activeEdit == null)
            Ui.Guard("Create Hole", () => lastEditor?.operations.NotifyError("Create Hole: " + reason + ". Shape unchanged."));
    }

    static bool eachFace; // inside EachFace: the game's own one-face hole runs as it is
    static bool holePerFace; // Hole per face: every selected face its own hole, even where they touch

    /// The game's Create Hole cuts one face (and its mirror image), taking the selected points as that face's corners,
    /// so with several faces selected it refused. Now selected faces that touch and lie flat together get one hole
    /// through the area they make (HoleMerge), and the rest (or all, with Hole per face) a hole each, one after another
    /// in the same edit (one Ctrl+Z undoes them all; one failing puts them all back). A face that an earlier face's
    /// mirror image already holed is skipped.
    [HarmonyPrefix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.ExecuteInternal))]
    static bool EachFace(CreateHoleOp __instance, EditMesh mesh)
    {
        if (eachFace || mesh == null) return true;
        var faces = new List<Face>();
        var all = mesh.faces;
        for (int i = 0; i < all.Count; i++) if (all[i].HasFlag(ElementFlags.Selected)) faces.Add(all[i]);
        if (faces.Count < 2) return true;
        eachFace = true;
        try
        {
            if (!holePerFace) faces = MergedHoles(__instance, mesh);
            int holes = 0;
            foreach (var face in faces)
            {
                if (activeEdit?.Failure != null) break;
                var now = mesh.faces;
                bool alive = false;
                for (int i = 0; i < now.Count && !alive; i++) alive = now[i].Pointer == face.Pointer;
                if (!alive) continue;
                MirrorMerge.Selection.Only(mesh, Corners(face, v => v).ToList());
                __instance.ExecuteInternal(mesh); // the game's own hole for this face (and its mirror image)
                holes++;
            }
            Plugin.ModLog.LogInfo($"Create Hole: {faces.Count} faces selected, {holes} holed one by one in one edit");
        }
        finally { eachFace = false; }
        return false;
    }

    /// Holes each area of two or more touching selected faces whole (and, with Mirror, its mirror image), and returns
    /// the selected faces left on their own.
    static List<Face> MergedHoles(CreateHoleOp op, EditMesh mesh)
    {
        var v = new MeshTools.View(mesh);
        var areas = HoleMerge.Areas(v.Pos, v.Corners, v.SelectedFaces);
        var merged = areas.Where(a => a.Count > 1).ToList();
        if (op.Symmetry) WithMirror(v, merged);
        if (merged.Count > 0)
        {
            var plan = HoleMerge.Plan(v.Pos, v.Corners, merged, segments, sizePercent / 100f, cardinalPoints, GameFill ? Fill.Mode.Fewest : (Fill.Mode)holeFill);
            // Rivets where the hole now is go, as with the game's own hole (2 mm), not onto its rim.
            var (done, message) = MeshTools.Apply(mesh, v, plan, "hole through the selected faces", gaps: false, rivetReach: 0.002f);
            if (done) Plugin.ModLog.LogInfo($"Create Hole: {merged.Sum(a => a.Count)} touching faces holed as {merged.Count} area(s): {message}");
            else Reject(message + "; Hole per face gives each face its own hole instead");
        }
        return areas.Where(a => a.Count == 1).Select(a => v.Faces[a[0]]).ToList();
    }

    /// Adds each area's mirror image across the middle, unless it's the area itself or already there.
    static void WithMirror(MeshTools.View v, List<List<int>> areas)
    {
        foreach (var area in areas.ToList())
        {
            var twin = FaceMerge.Mirrored(v.Pos, v.Corners, area, MeshTools.Tolerance);
            if (twin.Count == area.Count && !twin.Any(f => areas.Any(a => a.Contains(f)))) areas.Add(twin);
        }
    }

    /// While the mouse is over the game's Create Hole button: the holes it would cut (with Mirror, their mirror images
    /// too) outlined on the part, so there's nothing to try and undo.
    internal static void DrawHovered()
    {
        if (MeshTools.HoveredButton != "Create Hole" || Hotkeys.Current is not { } e || e.meshEditor.Mesh?.EditMesh is not { } mesh
            || !MeshSelection.TryMeshFrame(e, out var matrix) || UnityEngine.Camera.main is not { } camera || UnityEngine.Event.current.type != UnityEngine.EventType.Repaint) return;
        // Worked out once per hover (and again if the mesh, selection or settings change), not every frame.
        string key = $"{MeshTools.HoverCount}|{mesh.Pointer}|{mesh.vertices.Count}|{mesh.faces.Count}|{mesh.selectedFaceCount}|{e.meshEditor.Symmetry}|{segments}|{sizePercent}|{cardinalPoints}|{holePerFace}|{holeFill}"
            + $"|{MeshTools.SelectionFingerprint(mesh)}";
        if (hoverRings?.Key != key)
        {
            var v = new MeshTools.View(mesh);
            var areas = holePerFace ? v.SelectedFaces.Select(f => new List<int> { f }).ToList() : HoleMerge.Areas(v.Pos, v.Corners, v.SelectedFaces);
            if (e.meshEditor.Symmetry) WithMirror(v, areas);
            var points = new List<UnityEngine.Vector3>();
            var loops = new List<int[]>();
            foreach (var area in areas)
                if (HoleMerge.Ring(v.Pos, v.Corners, area, segments, sizePercent / 100f, cardinalPoints, out _) is { } hole)
                {
                    loops.Add(Enumerable.Range(points.Count, hole.Ring.Length).ToArray());
                    points.AddRange(hole.Ring.Select(p => new UnityEngine.Vector3(p.X, p.Y, p.Z)));
                }
            hoverRings = (key, points, loops);
        }
        if (hoverRings is { Points.Count: > 0 } rings) GeometryPreview.DrawWire(camera, rings.Points, rings.Loops, matrix, new UnityEngine.Color(1, .85f, .2f));
    }
    static (string Key, List<UnityEngine.Vector3> Points, List<int[]> Loops)? hoverRings;

    [HarmonyPrefix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.CreateHole))]
    static bool UseQuality(ref int resolution, EditMesh mesh, Il2CppReferenceArray<Vertex> vertices, ref Il2CppReferenceArray<Vertex> __result)
    {
        Plugin.ModLog.LogInfo($"Create Hole: game asked for {resolution} segments, using {segments} at {sizePercent}% size");
        resolution = segments;
        holeThickness = null;
        holeRivets = null;
        faceSide = null;
        originalSide = null;
        try
        {
            if (activeEdit?.Failure != null) { __result = new Il2CppReferenceArray<Vertex>(0); return false; }
            if (vertices == null || vertices.Length < 3 || vertices.Any(v => v == null) || vertices.Select(v => v.Pointer).Distinct().Count() != vertices.Length)
                throw new InvalidOperationException("select the corners of one face");
            if (vertices.Any(v => !Finite(ToNum(v.position)))) throw new InvalidOperationException("face has invalid points");
            var normal = UnityEngine.Vector3.zero;
            var centre = UnityEngine.Vector3.zero;
            var sorted = ElementSelection.SortVerticesOnRadialPlane(vertices.Cast<Il2CppSystem.Collections.Generic.IReadOnlyList<Vertex>>(), out normal, out centre);
            var face = Face.GetExistingFace(sorted, sorted.Length);
            // Cutting points spread across several faces adds a second plate over the old
            // plates: the native tool only deletes a face with exactly this corner loop.
            if (face == null) throw new InvalidOperationException("select one complete face, not points across several faces");
            var corners = sorted.Select(v => ToNum(v.position)).ToArray();
            var c = ToNum(centre);
            float radius = Enumerable.Range(0, corners.Length).Min(i => DistanceToSegment(c, corners[i], corners[(i + 1) % corners.Length])) * 0.9f;
            var side = HoleRing.Normal(corners);
            if (!Finite(c) || !Finite(side) || side.LengthSquared() < 1e-12f) throw new InvalidOperationException("face is too small for a hole");
            side = Num.Normalize(side);
            var u = Num.Normalize(new[] { Num.UnitY, Num.UnitZ }.Select(a => a - Num.Dot(a, side) * side).First(a => a.LengthSquared() > 0.1f));
            var v = Num.Cross(side, u);
            var seed = Enumerable.Range(0, segments).Select(k => c + radius * ((float)Math.Cos(k * Math.Tau / segments) * u + (float)Math.Sin(k * Math.Tau / segments) * v)).ToArray();
            if (!HoleRing.TryFit(corners, seed, c, out _, out string reason, sizePercent / 100f, cardinalPoints))
                throw new InvalidOperationException(reason);
            if (MeshPlans.HoleLimit(mesh.vertices.Count, mesh.edges.Count, mesh.faces.Count, mesh.loops.Count, corners.Length, segments) is string full)
                throw new InvalidOperationException(full);
            originalSide = HoleRing.Normal(Corners(face));
            holeThickness = face?.firstLoop?.thickness;
            // The game drops the holed face's rivets with it: noted here, put back on the faces round the hole after.
            if (face != null) holeRivets = new MeshTools.RivetKeeper(mesh, new[] { face });
            return true;
        }
        catch (Exception ex)
        {
            Reject(ex.Message);
            __result = new Il2CppReferenceArray<Vertex>(0);
            return false; // before native DeleteFace, CreateCircle or FillEdgeLoop
        }
    }

    /// The game's hole circle collapses on faces looking forward or back and shrinks on faces tilted that way
    /// (HoleRing.Circle): its points go where the game meant them. Create Hole is its only caller (both builds).
    [HarmonyPostfix, HarmonyPatch(typeof(MeshPrimitives), nameof(MeshPrimitives.CreateCircle))]
    static void RealCircle(int count, UnityEngine.Vector3 centre, UnityEngine.Vector3 normal, float radius, bool flipWinding, Il2CppReferenceArray<Vertex> __result)
    {
        if (__result == null || __result.Length != count || !Finite(ToNum(normal)) || ToNum(normal).LengthSquared() < 1e-12f) return;
        var ring = HoleRing.Circle(count, ToNum(centre), ToNum(normal), radius, flipWinding);
        for (int k = 0; k < count; k++) __result[k].position = new UnityEngine.Vector3(ring[k].X, ring[k].Y, ring[k].Z);
    }

    static bool Finite(Num p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    static float DistanceToSegment(Num p, Num a, Num b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-12f ? 0 : Math.Clamp(Num.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Num.Distance(p, a + t * ab);
    }

    static MeshTools.RivetKeeper? holeRivets;

    /// The holed face's rivets onto the faces now round the hole (those in the hole itself go).
    [HarmonyPostfix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.FillEdgeLoop))]
    static void KeepRivets(Il2CppReferenceArray<Face> __result)
    {
        try
        {
            var keeper = holeRivets;
            holeRivets = null;
            if (keeper is not { Count: > 0 } || __result == null || activeEdit?.Failure != null) return;
            var (kept, lost) = keeper.Place(__result, reach: 0.002f);
            Plugin.ModLog.LogInfo($"Create Hole: rivets {kept} kept, {lost} in the hole removed");
        }
        catch (Exception ex) { Reject(ex.Message); }
    }

    static Num? faceSide; // which way the face being holed really faces, to check the filled-in faces against
    static int holeFill; // index into FillNames: Fill.Mode's (fewest points first), then the game's own fan
    static readonly string[] FillNames = Fill.ModeNames.Select(n => "Faces: " + n).Append("Faces: original triangle fan").ToArray();
    static bool GameFill => holeFill == FillNames.Length - 1;

    /// Runs after the game has made the ring and before it fills the face around it: makes the ring a true circle,
    /// then (clean fill) builds the faces around it itself, a quad ring at the rim stepping out to the face's corners,
    /// instead of the game's fan of long thin triangles.
    [HarmonyPrefix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.FillEdgeLoop))]
    static bool TrueCircle(EditMesh mesh, Il2CppReferenceArray<Vertex> outer, Il2CppReferenceArray<Vertex> inner, UnityEngine.Vector3 centre, ref Il2CppReferenceArray<Face> __result)
    {
        Il2CppReferenceArray<Face>? mine = null;
        faceSide = null;
        try
        {
            var corners = outer.Select(v => ToNum(v.position)).ToArray();
            if (!HoleRing.TryFit(corners, inner.Select(v => ToNum(v.position)).ToArray(), ToNum(centre), out var ring, out var note, sizePercent / 100f, cardinalPoints))
                throw new InvalidOperationException(note);
            for (int k = 0; k < inner.Length; k++) inner[k].position = new UnityEngine.Vector3(ring[k].X, ring[k].Y, ring[k].Z);
            int order = originalSide is { } original ? (Num.Dot(HoleRing.Normal(corners), original) > 0 ? 1 : -1) : FaceOrder(outer);
            faceSide = order == 0 ? null : HoleRing.Normal(corners) * order;
            // The holed face is already gone by now: new faces copy a neighbour's settings and the holed face's thickness.
            string fill = GameFill ? "game's fill" : faceSide == null ? "game's fill (can't tell which way the face faces)"
                : (FaceOf(outer) ?? Neighbour(outer)) is not { } like ? "game's fill (no face next to it to copy settings from)"
                : (mine = CleanFill(mesh, outer, inner, faceSide.Value, order, like, holeThickness ?? like.firstLoop.thickness)) == null ? "game's fill (clean fill didn't fit)"
                : $"clean fill ({Fill.Paths[^1]}, {holeThickness?.ToString() ?? "neighbour's"} thickness)";
            Plugin.ModLog.LogInfo($"Create Hole ring: {note}; {fill}");
        }
        catch (Exception ex)
        {
            // Exceptions crossing an IL2CPP Harmony trampoline are swallowed. Record the
            // failure explicitly so EndHole restores the original; never run the fan on it.
            Reject(ex.Message);
            __result = new Il2CppReferenceArray<Face>(0);
            return false;
        }
        if (mine == null) return true;
        __result = mine;
        return false;
    }

    /// Builds the faces between the face's corners and the hole's ring with the mesh's own calls, turned the way the
    /// original face faces, copying its settings.
    static Il2CppReferenceArray<Face>? CleanFill(EditMesh mesh, Il2CppReferenceArray<Vertex> outer, Il2CppReferenceArray<Vertex> inner, Num normal, int order, Face prototype, ushort thickness)
    {
        var verts = outer.Concat(inner).ToList();
        var pos = verts.Select(v => ToNum(v.position)).ToList();
        var added = new List<Fill.Added>();
        // The fill turns its faces the way the outline runs: give it the corners in the face's own order.
        var corners = Enumerable.Range(0, outer.Length).ToList();
        if (order < 0) corners.Reverse();
        var mode = (Fill.Mode)holeFill;
        var faces = Fill.Region(pos, corners, new List<List<int>> { Enumerable.Range(outer.Length, inner.Length).ToList() }, normal,
                                mode == Fill.Mode.Fewest ? null : added, mode == Fill.Mode.Light, mode);
        if (HoleFill.Check(pos, corners, Enumerable.Range(outer.Length, inner.Length).ToArray(), faces, normal) is string problem)
            throw new InvalidOperationException("surrounding faces did not fit: " + problem);
        // Its added points can take it past the game's fan, checked before the hole started.
        long newEdges = faces.SelectMany(f => f.Select((i, k) => FaceMerge.Key(i, f[(k + 1) % f.Length]))).Distinct()
            .LongCount(e => e.Item2 >= verts.Count || Edge.GetConnectingEdge(verts[e.Item1], verts[e.Item2]) == null);
        if (MeshPlans.NativeLimit(mesh.vertices.Count + (long)added.Count, mesh.edges.Count + newEdges, mesh.faces.Count + (long)faces.Count,
                mesh.loops.Count + faces.Sum(f => (long)f.Length), "this hole would make the part") is string full)
            throw new InvalidOperationException(full);
        foreach (var a in added)
        {
            // CreateVertex copies its prototype's position, so place each new vertex after creating it.
            var at = new UnityEngine.Vector3(a.P.X, a.P.Y, a.P.Z);
            var v = mesh.CreateVertex(inner[0], at);
            v.position = at;
            verts.Add(v);
        }
        var created = new List<Face>();
        foreach (var f in faces)
        {
            var vs = f.Select(i => verts[i]).ToArray();
            var es = new Edge[vs.Length];
            for (int k = 0; k < vs.Length; k++)
            {
                if (Edge.GetConnectingEdge(vs[k], vs[(k + 1) % vs.Length]) is { } existing) es[k] = existing;
                else
                {
                    es[k] = mesh.CreateEdge(vs[k], vs[(k + 1) % vs.Length], null);
                    es[k].flags = ElementFlags.None;
                }
            }
            created.Add(mesh.CreateFace(new Il2CppReferenceArray<Vertex>(vs), new Il2CppReferenceArray<Edge>(es), prototype,
                new Il2CppStructArray<ushort>(vs.Select(_ => thickness).ToArray())));
        }
        return new Il2CppReferenceArray<Face>(created.ToArray());
    }

    /// Any face on the other side of one of the loop's edges.
    static Face? Neighbour(Il2CppReferenceArray<Vertex> outer)
    {
        for (int i = 0; i < outer.Length; i++)
        {
            var first = Edge.GetConnectingEdge(outer[i], outer[(i + 1) % outer.Length])?.loop;
            var l = first;
            for (int guard = 0; l != null && guard < 16; guard++)
            {
                if (l.face != null) return l.face;
                l = l.radialNext;
                if (l == null || l.Pointer == first!.Pointer) break;
            }
        }
        return null;
    }

    /// The face whose corners are exactly these vertices, found through the edge between the first two.
    static Face? FaceOf(Il2CppReferenceArray<Vertex> outer)
    {
        if (outer.Length < 3) return null;
        var mine = outer.Select(v => v.Pointer).ToHashSet();
        var first = Edge.GetConnectingEdge(outer[0], outer[1])?.loop;
        var l = first;
        for (int guard = 0; l != null && guard < 16; guard++)
        {
            if (IsFace(l.face, mine)) return l.face;
            l = l.radialNext;
            if (l == null || l.Pointer == first!.Pointer) break;
        }
        return null;
    }

    /// The game's fill turns some of its new faces inside out (a different few each time): turn those back round.
    [HarmonyPostfix, HarmonyPatch(typeof(CreateHoleOp), nameof(CreateHoleOp.FillEdgeLoop))]
    static void FixSides(Il2CppReferenceArray<Face> __result, Il2CppReferenceArray<Vertex> outer, Il2CppReferenceArray<Vertex> inner)
    {
        try
        {
            if (faceSide is not { } side || __result == null || activeEdit?.Failure != null) return;
            // The game's list can name a face twice; turning it twice would put it back inside out.
            var faces = __result.GroupBy(f => f.Pointer).Select(g => g.First()).ToList();
            var wrong = faces.Where(f => Num.Dot(HoleRing.Normal(Corners(f)), side) < 0).ToList();
            wrong.ForEach(Flip);
            var problems = faces.Select(Problem).Where(p => p != null).Distinct().ToList();
            // Also check the game's optional triangle fan. Sound native links do not prove
            // that every ring edge was used or that the faces actually leave an open hole.
            var positions = new List<Num>();
            var indices = new Dictionary<IntPtr, int>();
            int Index(Vertex vertex)
            {
                if (!indices.TryGetValue(vertex.Pointer, out int index))
                { indices[vertex.Pointer] = index = positions.Count; positions.Add(ToNum(vertex.position)); }
                return index;
            }
            var outerIds = outer.Select(Index).ToArray();
            if (Num.Dot(HoleRing.Normal(outer.Select(v => ToNum(v.position)).ToArray()), side) < 0) Array.Reverse(outerIds);
            var innerIds = inner.Select(Index).ToArray();
            var made = faces.Select(f => Corners(f, Index)).ToArray();
            if (HoleFill.Check(positions, outerIds, innerIds, made, side) is string geometry) problems.Add(geometry);
            int right = faces.Count(f => Num.Dot(HoleRing.Normal(Corners(f)), side) > 0);
            if (problems.Count > 0) Reject(string.Join("; ", problems));
            Plugin.ModLog.LogInfo($"Create Hole fill: turned {wrong.Count} of {faces.Count} faces the right way round, {right} now face out" +
                                  (problems.Count == 0 ? ", mesh checks OK" : ", MESH CHECK FAILED: " + string.Join("; ", problems)));
        }
        catch (Exception ex) { Reject(ex.Message); }
    }

    /// Reverses a face's corner order: each corner keeps its vertex, thickness and thicken edge, and moves onto the
    /// edge behind it. Done with the mesh's own link helpers.
    static void Flip(Face face)
    {
        var ring = new Loop[face.vertexCount];
        var l = face.firstLoop;
        for (int i = 0; i < ring.Length; i++, l = l.next) ring[i] = l;
        var edges = ring.Select(x => x.edge).ToArray();
        for (int i = 0; i < ring.Length; i++) Loop.RemoveLoopFromEdgeRadialCycle(edges[i], ring[i]);
        for (int i = 0; i < ring.Length; i++)
        {
            (ring[i].next, ring[i].prev) = (ring[i].prev, ring[i].next);
            ring[i].edge = edges[(i - 1 + ring.Length) % ring.Length];
        }
        for (int i = 0; i < ring.Length; i++) Loop.AddLoopToEdge(ring[i].edge, ring[i]);
        face.normal = -face.normal;
    }

    /// The game's own consistency checks for a face and its corners, plus "every corner's edge joins it to the next".
    internal static string? Problem(Face face)
    {
        var faceProblem = Face.Validate(face);
        if ((int)faceProblem != 0) return "face: " + faceProblem;
        var l = face.firstLoop;
        for (int i = 0; i < face.vertexCount; i++, l = l.next)
        {
            var loopProblem = Loop.Validate(l);
            if ((int)loopProblem != 0) return "corner: " + loopProblem;
            if (!Loop.ValidateRadialCycle(l)) return "corner not linked to its edge";
            if (!l.edge.ContainsVertices(l.vertex, l.next.vertex)) return "corner edge doesn't join it to the next corner";
        }
        return null;
    }

    /// +1 if the corners run the way the face does, -1 if backwards, 0 if it can't tell. Uses the face itself, or
    /// a neighbour across any of its edges (neighbours run a shared edge in opposite directions).
    static int FaceOrder(Il2CppReferenceArray<Vertex> outer)
    {
        var mine = outer.Select(v => v.Pointer).ToHashSet();
        int answer = 0;
        for (int i = 0; i < outer.Length && outer.Length >= 3; i++)
        {
            Vertex va = outer[i], vb = outer[(i + 1) % outer.Length];
            var first = Edge.GetConnectingEdge(va, vb)?.loop;
            var l = first;
            for (int guard = 0; l != null && guard < 16; guard++)
            {
                IntPtr from = l.vertex.Pointer, to = l.next.vertex.Pointer;
                int direction = from == va.Pointer && to == vb.Pointer ? 1 : from == vb.Pointer && to == va.Pointer ? -1 : 0;
                if (direction != 0 && IsFace(l.face, mine)) return direction;
                if (direction != 0 && answer == 0) answer = -direction;
                l = l.radialNext;
                if (l == null || l.Pointer == first!.Pointer) break;
            }
        }
        return answer;
    }

    static bool IsFace(Face? face, HashSet<IntPtr> corners) =>
        face != null && face.vertexCount == corners.Count && Corners(face, v => corners.Contains(v.Pointer)).All(x => x);

    internal static Num[] Corners(Face face) => Corners(face, v => ToNum(v.position));

    internal static T[] Corners<T>(Face face, Func<Vertex, T> pick)
    {
        var result = new T[face.vertexCount];
        var loop = face.firstLoop;
        for (int i = 0; i < result.Length; i++, loop = loop.next) result[i] = pick(loop.vertex);
        return result;
    }

    internal static Num ToNum(UnityEngine.Vector3 v) => new(v.x, v.y, v.z);

    internal static void Draw(PlateStructureEditor __instance, Panel layout) => Ui.Inspector("Hole quality", layout, () =>
    {
        var ui = Ui.Drawer(layout);
        if (ui == null || __instance.TryCast<FreeformPlateStructureEditor>() == null) return; // Create Hole is a freeform tool
        lastEditor = __instance;
        Ui.Section(layout, "Hole quality");
        ui.InfoField("Faces mode: touching faces get one hole through them,\nor one each with Hole per face. Ctrl+Z undoes it all.", 2);
        ui.ToggleField("Hole per face", holePerFace, Ui.BoolCallback(v => holePerFace = v),
            "On: every selected face gets its own hole. Off: selected faces that touch and lie flat together get one hole through the area they make.");
        ui.Slider("Circle segments", segments, 4, 96, Ui.FloatCallback(v => segments = (int)Math.Round(v)));
        ui.Button(cardinalPoints ? "Ring: cardinal points" : "Ring: flat sides", Ui.Callback(() =>
        {
            cardinalPoints = !cardinalPoints;
            __instance.RequestRedraw();
        }), new Tip("Hole ring alignment", "Cardinal points puts a vertex at the top, bottom, left and right with a segment count divisible by four (such as 32). Flat sides keeps QoL's previous half-segment rotation. Applies to new holes."));
        // Applied in HoleRing.Fit, never through the game's CreateHoleOp.HoleRadiusScale: see there.
        ui.Slider("Relative size (%)", sizePercent, 10, 300, Ui.FloatCallback(v => sizePercent = (int)Math.Round(v)));
        var tip = new Tip("Surrounding faces", "Click to cycle the faces around the hole. Fewest points uses the circle and existing corners. Light fill adds a few points so no faces are long and thin. Smooth fill adds a ring of quads round the hole and more points for even faces. Original triangle fan uses the game's layout. The circle stays inside the selected face.");
        // The panel only redraws when asked, so ask, or the button would keep showing the old choice.
        ui.Button(FillNames[holeFill], Ui.Callback(() => { holeFill = (holeFill + 1) % FillNames.Length; __instance.RequestRedraw(); }), tip);
    });
}
