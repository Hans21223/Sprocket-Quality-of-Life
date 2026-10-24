using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.MeshEditing;
using Sprocket.Factions;
using Sprocket.MeshEditing.Operations;
using Sprocket.PlateMesh;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using UnityEngine.InputSystem;
using N2 = System.Numerics.Vector2;

namespace SprocketQoL;

/// Changes only the hit query: native selection modes, undo, box selection and
/// transform routing continue to consume the normal SampleResult.
[HarmonyPatch]
internal static class MeshSelection
{
    static bool unavailable;
    static readonly CandidateCycle cycle = new();
    static readonly SelectionMemory selections = new();
    static SelectionSetStore? library;
    static readonly HashSet<(string Design,long Part,string Geometry)> hydrated=new();
    sealed record Hover(Vector2 A, Vector2 B, bool Edge, int Ordinal, int Count, int Frame);
    static Hover? hovered;
    static (Face Face, int Frame)? hoveredFace;
    static EditMesh? sampledMesh;
    static MeshOperatorContext? sampledContext;
    static int ticked = -1;
    static readonly Dictionary<IntPtr,(MeshSelectOp Op,Action<EditMesh> Apply)> recalls=new();
    static readonly Dictionary<IntPtr,IntPtr> recallHistory=new();

    // Recalls run as DeselectAllOps: MeshSelectOp's own ExecuteSelection is empty, and the game shares that lone `ret`
    // with about 4,000 other methods, so hooking it hooked them all (a crash at start).
    [HarmonyPrefix,HarmonyPatch(typeof(DeselectAllOp),nameof(DeselectAllOp.ExecuteSelection))]
    static bool ApplyNamedSelection(MeshSelectOp __instance,MeshOperatorContext context)
    {
        if(!recalls.TryGetValue(__instance.Pointer,out var recall))return true;
        Ui.Guard("Recall selection",()=>recall.Apply(context.editor.Mesh.EditMesh));
        return false;
    }
    [HarmonyPrefix,HarmonyPatch(typeof(Operations.Operation),nameof(Operations.Operation.ReleaseInternal))]
    static void ReleaseSelection(Operations.Operation __instance)
    {
        if(recallHistory.Remove(__instance.Pointer,out var op))recalls.Remove(op);
    }
    [HarmonyPrefix,HarmonyPatch(typeof(Operations.Operation),nameof(Operations.Operation.CancelInternal))]
    static void CancelSelection(Operations.Operation __instance)=>ReleaseSelection(__instance);
    // Unity samples on its main thread. Reuse the native broad-phase output instead
    // of allocating a native list, or reading every element through interop, each frame.
    static readonly Il2CppSystem.Collections.Generic.List<int> candidates = new();
    static (int Frame, IntPtr Mesh, ElementType Type, bool XRay, Vector2 Pointer, SampleResult Result)? lastSample;

    [HarmonyPrefix, HarmonyPatch(typeof(PickOp), nameof(PickOp.Sample))]
    static bool Sample(EditMesh mesh, MeshOperatorContext context, ElementType type, bool xRayMode, ref SampleResult __result)
    {
        if (unavailable || Plugin.BetterMeshPicking?.Value == false || type is not (ElementType.Vertex or ElementType.Edge))
        { hovered=null; return true; }
        try
        {
            var camera = context?.Observer;
            if (mesh == null || context == null || camera == null) return true;
            if (mesh.vertices.Count > ushort.MaxValue || mesh.edges.Count > ushort.MaxValue
                || mesh.faces.Count > ushort.MaxValue || mesh.loops.Count > ushort.MaxValue) return true;
            var size = context.ScreenSize;
            var pointer = context.PointerPosition;
            if (size.x <= 0 || size.y <= 0 || !Finite(pointer.x) || !Finite(pointer.y)) return true;
            if (pointer.x < 0 || pointer.y < 0 || pointer.x >= size.x || pointer.y >= size.y) return true;
            float requested = Plugin.MeshPickRadius?.Value ?? 12;
            float radius = Finite(requested) ? Math.Clamp(requested, 4, 30) : 12;
            float near = Math.Max(1e-5f, camera.nearClipPlane), far = camera.farClipPlane;
            if (!Finite(near) || !Finite(far) || far <= near) return true;
            sampledMesh = mesh; sampledContext = context;
            // Once a frame: the hover marks (Tick) and the editor both ask, and a second pass would also cycle twice.
            if (lastSample is { } last && last.Frame == Time.frameCount && last.Mesh == mesh.Pointer && last.Type == type
                && last.XRay == xRayMode && last.Pointer == pointer) { __result = last.Result; return false; }

            // SampleResult indices are list slots. Keep incident face/loop indices current as well: native plate/linked
            // selection can use those fields. Only when the mesh says they're out of date (every sample did all four).
            const ElementType indexed = ElementType.Vertex | ElementType.Edge | ElementType.Face | ElementType.Loop;
            if ((mesh.GetIndexDirtyFlags() & indexed) != 0) mesh.ReIndex(indexed);
            var projection = Matrix4x4.identity;
            context.GetPerspectiveMatrix(ref projection); // P * V, without the mesh transform
            projection *= context.MeshToWorld;
            var view = context.ViewMatrix * context.MeshToWorld;
            float Depth(Vector3 p) => -(view.m20 * p.x + view.m21 * p.y + view.m22 * p.z + view.m23);
            N2 Project(Vector3 p)
            {
                float w = projection.m30 * p.x + projection.m31 * p.y + projection.m32 * p.z + projection.m33;
                float x = projection.m00 * p.x + projection.m01 * p.y + projection.m02 * p.z + projection.m03;
                float y = projection.m10 * p.x + projection.m11 * p.y + projection.m12 * p.z + projection.m13;
                return new((x / w + 1) * .5f * size.x, (y / w + 1) * .5f * size.y);
            }
            bool Visible(Vector3 target, MeshPicking.Hit hit)
            {
                if (xRayMode) return true;
                // In orthographic view each pixel has its own ray origin. Using
                // camera.position here would hide otherwise visible points.
                var ray = camera.ViewportPointToRay(new Vector3(hit.ClosestPixel.X / size.x, hit.ClosestPixel.Y / size.y, 0));
                var origin = context.WorldToMesh.MultiplyPoint3x4(ray.origin);
                var direction = context.WorldToMesh.MultiplyVector(ray.direction).normalized;
                float distance = Vector3.Dot(target - origin, direction);
                if (!Finite(distance)) return false;
                float tolerance = Math.Max(1e-5f, Math.Abs(distance) * 1e-6f);
                if (distance < -tolerance) return false;
                return distance <= tolerance || !MeshSampling.RaycastFaces(mesh, origin, direction,
                    distance - tolerance, out MeshSampling.ElementHit face, FaceMode.Both);
            }
            var endpoints = new Dictionary<int, (Vector3 A, Vector3 B)>();
            MeshPicking.ScreenEdge? ProjectEdge(Edge edge, int id)
            {
                if (edge.HasFlag(ElementFlags.Delete)) return null;
                var a = edge.v0.position; var b = edge.v1.position;
                float za = Depth(a), zb = Depth(b);
                if (!MeshPicking.TryClipNear(za, zb, near, out var clip) || za > far && zb > far) return null;
                float from = clip.From, to = clip.To;
                if (za > far) from = Math.Max(from, (far - za) / (zb - za));
                if (zb > far) to = Math.Min(to, (far - za) / (zb - za));
                if (from > to) return null;
                var clippedA = Vector3.LerpUnclamped(a, b, from);
                var clippedB = Vector3.LerpUnclamped(a, b, to);
                var screenA = Project(clippedA); var screenB = Project(clippedB);
                if (!Finite(screenA.X) || !Finite(screenA.Y) || !Finite(screenB.X) || !Finite(screenB.Y)
                    || Math.Max(screenA.X, screenB.X) < pointer.x - radius || Math.Min(screenA.X, screenB.X) > pointer.x + radius
                    || Math.Max(screenA.Y, screenB.Y) < pointer.y - radius || Math.Min(screenA.Y, screenB.Y) > pointer.y + radius) return null;
                endpoints[id] = (a, b);
                return new(id, screenA, screenB, Depth(clippedA), Depth(clippedB), From: from, To: to);
            }
            bool EdgeVisible(MeshPicking.Hit hit) => Visible(
                Vector3.LerpUnclamped(endpoints[hit.Id].A, endpoints[hit.Id].B, hit.SegmentT), hit);

            candidates.Clear();
            var result = Empty(type);
            IReadOnlyList<MeshPicking.Hit> hits = Array.Empty<MeshPicking.Hit>();
            int ordinal = -1;
            MeshPicking.Hit? Choose(IReadOnlyList<MeshPicking.Hit> eligible)
            {
                hits = eligible;
                bool advance = MeshAssistSettings.CyclePicking?.Value != false && Keybinds.Pressed("cycleSelection") && !Blocked();
                string scope = $"{DesignEditor.Instance?.Core?.Target?.Pointer}:{context.editor?.Pointer}:{mesh.Pointer}:{type}:{xRayMode}";
                ordinal = cycle.Choose(scope, new(pointer.x, pointer.y), eligible.Select(h => h.Id).ToArray(), advance, Time.frameCount);
                return ordinal < 0 ? null : eligible[ordinal];
            }
            if (type == ElementType.Vertex)
            {
                MeshSampling.GetVerticesInRadius(mesh, ref projection, pointer, size, radius, candidates);
                var points = new List<MeshPicking.ScreenPoint>(candidates.Count);
                var positions = new Dictionary<int, Vector3>(candidates.Count);
                for (int k = 0; k < candidates.Count; k++)
                {
                    int id = candidates[k];
                    if (id < 0 || id >= mesh.vertices.Count) continue;
                    var vertex = mesh.vertices[id];
                    if (vertex.HasFlag(ElementFlags.Delete)) continue;
                    var p = vertex.position;
                    float depth = Depth(p);
                    if (depth < near || depth > far) continue;
                    positions[id] = p;
                    points.Add(new(id, Project(p), depth));
                }
                var hit = Choose(MeshPicking.PointHits(points, new(pointer.x, pointer.y), radius,
                    accept: h => Visible(positions[h.Id], h), limit: MeshAssistSettings.CyclePicking?.Value == false ? 1 : 32));
                if (hit is { } picked)
                {
                    var at = Project(positions[picked.Id]);
                    hovered = new(new(at.X, at.Y), new(at.X, at.Y), false, ordinal, hits.Count, Time.frameCount);
                    result.vertexIndex = picked.Id;
                    // Loop selection from a vertex also needs a predictable incident
                    // edge. Choose its screen distance rather than native disk order.
                    var incidentEdges = new List<MeshPicking.ScreenEdge>();
                    var vertex = mesh.vertices[picked.Id];
                    var first = vertex.firstEdge;
                    var edge = first;
                    for (int i = 0; edge != null && i < mesh.edges.Count; i++)
                    {
                        if (ProjectEdge(edge, edge.index) is { } projected) incidentEdges.Add(projected);
                        edge = edge.GetNextDiskEdge(vertex);
                        if (edge == null || edge.Pointer == first?.Pointer) break;
                    }
                    var linkedEdge = MeshPicking.PickEdge(incidentEdges, new(pointer.x, pointer.y), radius,
                        camera.orthographic, accept: EdgeVisible);
                    if (linkedEdge is { } along) result.edgeIndex = along.Id;
                    Incident(mesh, context, ref result);
                }
            }
            else
            {
                // The native broad phase is fast, but cannot clip an edge crossing
                // the camera. Scan all edges only when the local mesh bounds cross it.
                bool crossesNear = true;
                if (context.editor?.Mesh is { } target)
                {
                    var bounds = target.LocalBounds;
                    float centre = Depth(bounds.center);
                    var extent = bounds.extents;
                    float reach = Math.Abs(view.m20) * extent.x + Math.Abs(view.m21) * extent.y + Math.Abs(view.m22) * extent.z;
                    crossesNear = !Finite(centre) || !Finite(reach) || centre - reach <= near + 1e-5f;
                }
                if (crossesNear)
                    for (int i = 0; i < mesh.edges.Count; i++) candidates.Add(i);
                else MeshSampling.GetEdgesInRadius(mesh, ref projection, pointer, size, radius, candidates);
                var edges = new List<MeshPicking.ScreenEdge>(candidates.Count);
                for (int k = 0; k < candidates.Count; k++)
                {
                    int id = candidates[k];
                    if (id < 0 || id >= mesh.edges.Count) continue;
                    if (ProjectEdge(mesh.edges[id], id) is { } projected) edges.Add(projected);
                }
                var hit = Choose(MeshPicking.EdgeHits(edges, new(pointer.x, pointer.y), radius, camera.orthographic,
                    accept: EdgeVisible, limit: MeshAssistSettings.CyclePicking?.Value == false ? 1 : 32));
                if (hit is { } picked)
                {
                    var projectedEdge = edges.First(e => e.Id == picked.Id);
                    hovered = new(new(projectedEdge.A.X, projectedEdge.A.Y), new(projectedEdge.B.X, projectedEdge.B.Y), true,
                        ordinal, hits.Count, Time.frameCount);
                    result.edgeIndex = picked.Id;
                    var edge = mesh.edges[picked.Id];
                    result.vertexIndex = (picked.SegmentT <= .5f ? edge.v0 : edge.v1).index;
                    Incident(mesh, context, ref result);
                }
            }
            if (ordinal < 0) hovered = null;
            __result = result;
            lastSample = (Time.frameCount, mesh.Pointer, type, xRayMode, pointer, result);
            return false;
        }
        catch (Exception ex)
        {
            unavailable = true;
            Plugin.ModLog.LogWarning($"Better point and edge picking is off for this session; using the game's picker. {ex}");
            return true;
        }
    }

    static bool Blocked() => DesignEditor.Instance?.IsReady != true || DesignEditor.Instance.IsBusy
        || DesignEditor.Instance.Core?.Editor?.OperationInProgress == true || InputShield.Claimed
        || EditorModal.BlocksInput
        || QolMenu.IsOpen || RightClickMenu.Open || ObjTransfer.BlocksInput || MeshTools.Typing()
        || PhotoShot.Capturing || DrawingSheet.Capturing;

    internal static void Tick() => Ui.Guard("Selection assist", () =>
    {
        if (ticked == Time.frameCount) return;
        ticked = Time.frameCount;
        // Ask the editor what's under the mouse every frame (it only asks itself on some frames): points and edges come
        // back through Sample above, faces through SampledFace. Only when something uses it: the hover marks, or cycling
        // (its key is read inside Sample, so a frame without one misses the press).
        // Not before the editor's pointer context is set up (while a design loads it isn't, and sampling then throws).
        if (HoverBlocked() || Ui.PointerOverGameUi() || Hotkeys.Current is not { } editor || editor.meshEditor?.Mesh?.EditMesh == null
            || editor.meshEditor.context?.operatorContext?.observer == null) return;
        bool cycling = !unavailable && Plugin.BetterMeshPicking?.Value != false && MeshAssistSettings.CyclePicking?.Value != false
            && editor.meshEditor.SelectType is MeshEditType.Vertex or MeshEditType.Edge;
        if (MeshAssistSettings.HoverHighlight?.Value == false && !cycling) return;
        editor.meshEditor.Sample(editor.operations.XRayEnabled);
    });

    /// The game samples the face under the mouse each frame while it's over the 3D view (and on a click): kept for Draw.
    [HarmonyPostfix, HarmonyPatch(typeof(PickOp), nameof(PickOp.Sample))]
    static void SampledFace(EditMesh mesh, ElementType type, SampleResult __result)
    {
        if (type == ElementType.Face && mesh != null && __result.faceIndex >= 0 && __result.faceIndex < mesh.faces.Count)
            hoveredFace = (mesh.faces[__result.faceIndex], Time.frameCount);
    }

    // Hover marks wait only for QoL's own windows and captures (the game's "busy" and Unity's "over UI" checks hid
    // them while just hovering); over the game's UI they're left out in Draw.
    static bool HoverBlocked() => DesignEditor.Instance?.IsReady != true || InputShield.Claimed || EditorModal.BlocksInput || QolMenu.IsOpen
        || RightClickMenu.Open || ObjTransfer.BlocksInput || PhotoShot.Capturing || DrawingSheet.Capturing;

    internal static void Draw()
    {
        bool face = hoveredFace is { } f && Time.frameCount - f.Frame <= 1;
        if (MeshAssistSettings.HoverHighlight?.Value == false || !face && (hovered is not { } || Time.frameCount-hovered.Frame > 1)
            || Event.current.type != EventType.Repaint || HoverBlocked()) return;
        var pointer = Mouse.current?.position.ReadValue();
        if (pointer == null || Ui.PointerOverGameUi()) return;
        if (face)
        {
            if (Hotkeys.Current is { } e && TryMeshFrame(e, out var frame) && Camera.main is { } camera)
            {
                var corners = HoleQuality.Corners(hoveredFace!.Value.Face, v => v.position);
                GeometryPreview.DrawWire(camera, corners, new[] { Enumerable.Range(0, corners.Length).ToArray() }, frame, new Color(1, .85f, .2f));
            }
            return;
        }
        var h = hovered!;
        // GL lines (GeometryPreview): the game's build stripped GUI.DrawTexture and GUIUtility.RotateAroundPivot.
        Vector2 a = new(h.A.x, h.A.y), b = new(h.B.x, h.B.y);
        var yellow = new Color(1, .85f, .2f);
        if (h.Edge) GeometryPreview.DrawScreenLines(new[] { a, b }, yellow);
        else
        {
            Vector2 c1 = a + new Vector2(-6, -6), c2 = a + new Vector2(6, -6), c3 = a + new Vector2(6, 6), c4 = a + new Vector2(-6, 6);
            GeometryPreview.DrawScreenLines(new[] { c1, c2, c2, c3, c3, c4, c4, c1 }, yellow, 2);
        }
        if (h.Count > 1) GUI.Label(new Rect(pointer.Value.x+14,Screen.height-pointer.Value.y+10,245,28),
            $"{h.Ordinal+1}/{h.Count} targets · {Keybinds.Shown("cycleSelection")} cycles");
    }

    static SelectionMemory.Scope Scope(PlateStructureEditor editor, EditMesh mesh)
    {
        mesh.ReIndex(ElementType.Vertex|ElementType.Edge|ElementType.Face|ElementType.Loop);
        string design = DesignEditor.Instance?.Core?.Target?.Pointer.ToString() ?? "";
        long part = (long)editor.Component.VehicleObject.VUID;
        var edges=mesh.edges.ToArray().Select(e => ((int)e.v0.index,(int)e.v1.index)).ToArray();
        var faces=mesh.faces.ToArray().Select(f => HoleQuality.Corners(f,v => (int)v.index)).ToArray();
        var identities=mesh.vertices.ToArray().Select(v => v.Pointer.ToInt64()).ToArray();
        return new(design,part,SelectionMemory.Fingerprint(mesh.vertices.Count,edges,faces,identities));
    }
    static string Geometry(EditMesh mesh)=>SelectionMemory.GeometryFingerprint(
        mesh.vertices.ToArray().Select(v=>new System.Numerics.Vector3(v.position.x,v.position.y,v.position.z)).ToArray(),
        mesh.edges.ToArray().Select(e=>((int)e.v0.index,(int)e.v1.index)).ToArray(),
        mesh.faces.ToArray().Select(f=>HoleQuality.Corners(f,v=>(int)v.index)).ToArray());
    static string StableDesign()=>SelectionSetStore.DesignKey(FactionManager.CurrentFaction?.Directory??"",
        DesignEditor.Instance?.Core?.Target?.TryCast<Sprocket.Vehicles.Vehicle>()?.DesignInfo?.Name??"");
    internal static void LoadStorage(string bepInExRoot)
    {
        library=new SelectionSetStore(Path.Combine(bepInExRoot,"SprocketQoL","selection-sets.json"));
        if(library.LoadMessage.Length>0)Plugin.ModLog.LogWarning(library.LoadMessage);
    }
    static void EnsureLoaded(PlateStructureEditor editor,EditMesh mesh)
    {
        if(library==null)return;
        string design=StableDesign();
        if(design.Length==0)return;
        var scope=Scope(editor,mesh);string geometry=Geometry(mesh);
        // Once per shape, not per part: a set skipped because the shape had changed loads when Ctrl+Z brings that shape back.
        if(!hydrated.Add((DesignEditor.Instance?.Core?.Target?.Pointer.ToString()??"",(long)editor.Component.VehicleObject.VUID,geometry)))return;
        foreach(var item in library.ForPart(design,scope.Part))
            if(item.Geometry==geometry && Enum.IsDefined(typeof(MeshEditType),(MeshEditType)item.Mode)
                &&item.Points.All(i=>i<mesh.vertices.Count)&&item.Edges.All(i=>i<mesh.edges.Count)&&item.Faces.All(i=>i<mesh.faces.Count))
                selections.Save(item.Name,scope,item.Mode,item.Points,item.Edges,item.Faces,out _);
    }
    static bool Ready(out PlateStructureEditor editor, out EditMesh mesh)
    {
        editor=Hotkeys.Current!; mesh=editor?.meshEditor?.Mesh?.EditMesh!;
        // These APIs are explicit buttons in the selection manager, so its own
        // modal is allowed. Only raw viewport/key sampling uses Blocked().
        return editor!=null && mesh!=null && DesignEditor.Instance?.IsReady==true && !DesignEditor.Instance.IsBusy
            && DesignEditor.Instance.Core?.Editor?.OperationInProgress!=true && !PhotoShot.Capturing && !DrawingSheet.Capturing;
    }
    internal static string[] SetNames()
    {
        if (!Ready(out var editor,out var mesh)) return Array.Empty<string>();
        EnsureLoaded(editor,mesh);
        long part=(long)editor.Component.VehicleObject.VUID;
        return selections.Names(DesignEditor.Instance?.Core?.Target?.Pointer.ToString()??"",part)
            .Concat(library?.Names(StableDesign(),part)??Array.Empty<string>()).Distinct().OrderBy(n=>n,StringComparer.Ordinal).ToArray();
    }
    internal static void SaveSet(string name) => Ui.Guard("Save selection", () =>
    {
        if (!Ready(out var editor,out var mesh)) return;
        EnsureLoaded(editor,mesh);var scope=Scope(editor,mesh);int mode=(int)editor.meshEditor.SelectType;
        var points=mesh.vertices.ToArray().Where(v=>v.HasFlag(ElementFlags.Selected)).Select(v=>(int)v.index).ToArray();
        var edges=mesh.edges.ToArray().Where(v=>v.HasFlag(ElementFlags.Selected)).Select(v=>(int)v.index).ToArray();
        var faces=mesh.faces.ToArray().Where(v=>v.HasFlag(ElementFlags.Selected)).Select(v=>(int)v.index).ToArray();
        bool saved=selections.Save(name,scope,mode,points,edges,faces,out var message);
        if(saved && library!=null)
        {
            string stable=StableDesign();
            if(stable.Length>0)library.Save(new(name.Trim(),stable,scope.Part,Geometry(mesh),mode,points,edges,faces),out message);
            else message+=" Disk storage needs a named design in a faction.";
        }
        DesignEditor.Instance?.Say(message);
    });
    internal static void RecallSet(string name, bool add=false) => Ui.Guard("Recall selection", () =>
    {
        if (!Ready(out var editor,out var mesh)) return;
        EnsureLoaded(editor,mesh);
        var saved=selections.Recall(name,Scope(editor,mesh),mesh.vertices.Count,mesh.edges.Count,mesh.faces.Count,out var message);
        if (saved==null)
        {
            if(library?.Contains(name,StableDesign(),(long)editor.Component.VehicleObject.VUID)==true)
                message="This selection was saved on this part's shape before it changed (points moved, added or removed). Undo back to that shape, or reopen the unchanged design, to recall it; or save it again.";
            DesignEditor.Instance?.Say(message); return;
        }
        if(!Enum.IsDefined(typeof(MeshEditType),(MeshEditType)saved.Mode)){DesignEditor.Instance?.Say("This selection mode is not supported by this game build.");return;}
        bool done=SelectAsStep(editor,"Recall selection",(MeshEditType)saved.Mode,mesh =>
        {
            foreach(int id in saved.Points) mesh.vertices[id].EnableFlag(ElementFlags.Selected);
            foreach(int id in saved.Edges) mesh.edges[id].EnableFlag(ElementFlags.Selected);
            foreach(int id in saved.Faces) mesh.faces[id].EnableFlag(ElementFlags.Selected);
        },add);
        if(done)DesignEditor.Instance?.Say(message);
    });

    /// Selects with one of the game's own selection steps, so Ctrl+Z gives back the selection before: `select` marks the
    /// elements on the shape, after everything was deselected (unless `add`). False if the editor is busy.
    internal static bool SelectAsStep(PlateStructureEditor editor,string name,MeshEditType mode,Action<EditMesh> select,bool add=false)
    {
        if(MeshTools.Busy(editor,name))return false;
        // Made by the editor, which gives the step its mesh and context: one made with `new` has none, and the game's
        // selection step threw "mesh is null" before ours ran.
        var op=editor.meshEditor.DeselectAll();op.Name=name;
        recalls[op.Pointer]=(op,mesh =>
        {
            if (!add)
            {
                foreach(var v in mesh.vertices) v.DisableFlag(ElementFlags.Selected);
                foreach(var e in mesh.edges) e.DisableFlag(ElementFlags.Selected);
                foreach(var f in mesh.faces) f.DisableFlag(ElementFlags.Selected);
            }
            select(mesh);
            editor.meshEditor.SelectType=mode;
            mesh.MarkDirty(MeshDirtyFlags.All);
        });
        try
        {
            var carried=editor.operations.Execute(op,StructureEditOperationOptions.None,editor.operations.GetNewGroupID());
            if(carried?.operation is { } wrapper && !carried.cancelled && wrapper.State!=Operations.OperatorState.Released)
                recallHistory[wrapper.Pointer]=op.Pointer;
            else recalls.Remove(op.Pointer);
            editor.meshEditor.SelectFlush();
            return true;
        }
        catch {recalls.Remove(op.Pointer);throw;}
    }
    internal static void DeleteSet(string name) => Ui.Guard("Delete selection", () =>
    {
        if (!Ready(out var editor,out var mesh)) return;
        long part=(long)editor.Component.VehicleObject.VUID;
        bool removed=selections.Delete(name,DesignEditor.Instance?.Core?.Target?.Pointer.ToString()??"",part);
        if(library!=null)
        {
            if(library.Delete(name,StableDesign(),part,out var message)||message.Length>0){DesignEditor.Instance?.Say(message);return;}
        }
        if(removed)DesignEditor.Instance?.Say($"Deleted selection '{name}' from this session.");
    });
    internal static void LeftEditor()
    {
        selections.Clear(); hydrated.Clear(); cycle.Clear(); recalls.Clear(); recallHistory.Clear(); hovered=null; sampledMesh=null; sampledContext=null; ticked=-1;
    }
    internal static bool TryMeshFrame(PlateStructureEditor editor, out Matrix4x4 localToWorld)
    {
        localToWorld=Matrix4x4.identity;
        if (sampledContext != null && sampledMesh != null && editor.meshEditor.Pointer == sampledContext.editor?.Pointer
            && editor.meshEditor.Mesh?.EditMesh?.Pointer == sampledMesh.Pointer)
        { localToWorld=sampledContext.MeshToWorld; return true; }
        var structure = editor.Component;
        if (structure == null) return false;
        var origin = structure.StructureToWorldSpace(Vector3.zero);
        var x = structure.StructureToWorldSpace(Vector3.right)-origin;
        var y = structure.StructureToWorldSpace(Vector3.up)-origin;
        var z = structure.StructureToWorldSpace(Vector3.forward)-origin;
        localToWorld = new Matrix4x4(new Vector4(x.x,x.y,x.z,0),new Vector4(y.x,y.y,y.z,0),new Vector4(z.x,z.y,z.z,0),new Vector4(origin.x,origin.y,origin.z,1));
        return true;
    }

    static bool Finite(float n) => float.IsFinite(n);
    static SampleResult Empty(ElementType type) => new()
        { type = type, faceIndex = -1, loopIndex = -1, vertexIndex = -1, edgeIndex = -1 };
    static void Incident(EditMesh mesh, MeshOperatorContext context, ref SampleResult result)
    {
        var incident = new Dictionary<int, Loop>();
        void OnEdge(Edge edge, Vertex? vertex)
        {
            var first = edge.loop;
            var loop = first;
            for (int i = 0; loop != null && i < mesh.loops.Count; i++)
            {
                var at = loop;
                if (vertex != null && at.vertex.Pointer != vertex.Pointer) at = at.next;
                if (at.face != null && !at.face.HasFlag(ElementFlags.Delete)
                    && (vertex == null || at.vertex.Pointer == vertex.Pointer)) incident.TryAdd(at.face.index, at);
                loop = loop.radialNext;
                if (loop == null || loop.Pointer == first?.Pointer) break;
            }
        }
        if (result.type == ElementType.Edge) OnEdge(mesh.edges[result.edgeIndex], null);
        else
        {
            var vertex = mesh.vertices[result.vertexIndex];
            var first = vertex.firstEdge;
            var edge = first;
            for (int i = 0; edge != null && i < mesh.edges.Count; i++)
            {
                OnEdge(edge, vertex);
                edge = edge.GetNextDiskEdge(vertex);
                if (edge == null || edge.Pointer == first?.Pointer) break;
            }
        }
        // Preserve native plate-selection intent: the incident face under the cursor,
        // rather than whichever face happened to be first in an edge's radial list.
        var ray = context.PointerRay;
        var origin = context.WorldToMesh.MultiplyPoint3x4(ray.origin);
        var direction = context.WorldToMesh.MultiplyVector(ray.direction).normalized;
        Loop? chosen = null;
        float closest = float.PositiveInfinity, facing = float.NegativeInfinity;
        foreach (var pair in incident.OrderBy(p => p.Key))
        {
            if (MeshSampling.RaycastFace(mesh, pair.Key, origin, direction, float.MaxValue, out float distance, FaceMode.Both)
                && distance >= 0 && distance < closest) { chosen = pair.Value; closest = distance; }
            if (!float.IsPositiveInfinity(closest)) continue;
            float score = -Vector3.Dot(pair.Value.face.normal, direction);
            if (score > facing) { chosen = pair.Value; facing = score; }
        }
        var loop = chosen;
        if (loop?.face == null) return;
        result.faceIndex = loop.face.index;
        result.loopIndex = loop.index;
        if (result.type == ElementType.Vertex && result.edgeIndex < 0) result.edgeIndex = loop.edge.index;
    }
}
