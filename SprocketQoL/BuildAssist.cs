using HarmonyLib;
using Il2CppInterop.Runtime;
using Sprocket.MeshEditing;
using Sprocket.PlateMesh;
using Sprocket.PlateMesh.Operations;
using Sprocket.Transformations;
using Sprocket.Vehicles.PlateStructures.Design;
using Sprocket.Vehicles.PlateStructures;
using UnityEngine;
using UnityEngine.InputSystem;
using Num = System.Numerics.Vector3;
using N2 = System.Numerics.Vector2;

namespace SprocketQoL;

/// Passive movement suggestions, applied only while the user's binding is held.
/// Positions stay in native transform blocks so one native move owns undo/redo.
[HarmonyPatch]
internal static class BuildAssist
{
    enum Kind { Point, Edge, Face, EdgePoint, FacePoint }
    sealed record Target(BuildPrediction.Target Value, Kind Kind, bool Other = false);
    sealed record Segment(int Id, Num A, Num B, bool Other = false);
    sealed record Triangle(int Id, Num A, Num B, Num C, bool Other = false);
    sealed record Occluder(EditMesh Mesh, Matrix4x4 ActiveToLocal, Bounds Bounds);
    sealed class Session
    {
        public Transformation Owner = null!;
        public IntPtr Mesh;
        public int[] Indices = Array.Empty<int>();
        public Num Anchor;
        public int AnchorSlot = -1;
        public TransformOptions CentreFlags;
        public List<Target> Targets = new();
        public Num[] Original = Array.Empty<Num>();
        public List<int[]> Faces = new();
        public BuildPrediction.Hit? Previous;
        public Num[]? Final;
        public List<Segment> Segments = new();
        public List<Triangle> Triangles = new();
        public List<Occluder> Occluders = new();
        public int? PinnedTarget;
        public int LastCycleFrame = -1;
    }
    sealed record Guide(Camera Camera, Vector3 Source, Vector3 Destination, Vector3 Target, string Text, int Frame, bool Applied);
    static readonly Dictionary<IntPtr, Session> sessions = new();
    static readonly Dictionary<IntPtr, MeshGeometryEditOp> history = new();
    static MeshGeometryEditOp? live;
    static MeshOperatorContext? context;
    static IntPtr replay;
    static Guide? guide;
    static GUIStyle? label;

    [HarmonyPostfix, HarmonyPatch(typeof(PlateStructureEditOperations), nameof(PlateStructureEditOperations.Execute),
        new[] { typeof(MeshEditOp), typeof(StructureEditOperationOptions), typeof(int) })]
    static void Track(MeshEditOp __0, StructureEdit __result) => Ui.Guard("Predictive build history", () =>
    {
        if (__0.TryCast<MeshGeometryEditOp>() is not { } op || op.mode != MeshGeometryEditType.Translate
            || __result?.operation is not { } wrapper || __result.cancelled || wrapper.State == Operations.OperatorState.Released) return;
        if (op.state == MeshEditOperatorState.Cancelled || op.state == MeshEditOperatorState.Executed
            && !sessions.ContainsKey(op.transformation?.Pointer ?? IntPtr.Zero)) return;
        history[wrapper.Pointer] = op;
    });

    // Undo keeps its record. Only permanent history removal/cancellation releases it.
    [HarmonyPrefix, HarmonyPatch(typeof(Operations.Operation), nameof(Operations.Operation.ReleaseInternal))]
    static void Released(Operations.Operation __instance) => Release(__instance);
    [HarmonyPrefix, HarmonyPatch(typeof(Operations.Operation), nameof(Operations.Operation.CancelInternal))]
    static void Cancelled(Operations.Operation __instance) => Release(__instance);
    static void Release(Operations.Operation wrapper) => Ui.Guard("Predictive build history", () =>
    {
        if (history.Remove(wrapper.Pointer, out var op))
        {
            sessions.Remove(op.transformation?.Pointer ?? IntPtr.Zero);
            guide = null;
        }
    });

    static void Forget(MeshGeometryEditOp op)
    {
        sessions.Remove(op.transformation?.Pointer ?? IntPtr.Zero);
        foreach (var key in history.Where(p => p.Value.Pointer == op.Pointer).Select(p => p.Key).ToArray()) history.Remove(key);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Update))]
    static void Updating(MeshGeometryEditOp __instance, MeshOperatorContext context)
    {
        live = __instance.mode == MeshGeometryEditType.Translate ? __instance : null;
        BuildAssist.context = live == null ? null : context;
        guide = null;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Update))]
    static void Updated(MeshGeometryEditOp __instance, MeshEditOperatorState __result)
    {
        if (__result is MeshEditOperatorState.Cancelled or MeshEditOperatorState.Executed)
        {
            guide = null;
            if (__instance.transformation is not { } op || !sessions.TryGetValue(op.Pointer, out var s)
                || __result == MeshEditOperatorState.Cancelled || s.Final == null) Forget(__instance);
            else
            {
                s.Targets = new(); s.Original = Array.Empty<Num>(); s.Faces = new(); s.Previous = null;
                s.Segments = new(); s.Triangles = new(); s.Occluders = new(); s.PinnedTarget=null;
            }
        }
        if (live?.Pointer == __instance.Pointer) { live = null; context = null; }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Execute))]
    static void Executing(MeshGeometryEditOp __instance)
    {
        replay = __instance.mode == MeshGeometryEditType.Translate ? __instance.transformation?.Pointer ?? IntPtr.Zero : IntPtr.Zero;
        guide = null;
    }
    [HarmonyPostfix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Execute))]
    static void Executed() => replay = IntPtr.Zero;

    [HarmonyPostfix, HarmonyPatch(typeof(MeshGeometryEditOp), nameof(MeshGeometryEditOp.Revert))]
    static void Reverted() => guide = null;

    static bool Blocked() => DesignEditor.Instance?.IsReady != true || DesignEditor.Instance.IsBusy
        || EditorModal.BlocksInput
        || QolMenu.IsOpen || RightClickMenu.Open || ObjTransfer.BlocksInput || MeshTools.Typing()
        || PhotoShot.Capturing || DrawingSheet.Capturing;

    internal static void AfterMove(MeshTransformation transform, Transformation op)
    {
        if (op.blocks is not { Length: 1 } blocks || blocks[0] is not { } block || block.elements.Length == 0) return;
        sessions.TryGetValue(op.Pointer, out var session);
        if (replay == op.Pointer)
        {
            // Execute is also used by redo. A key released, or the option switched
            // off since the edit, must not change the move that history replays.
            if (session?.Final is { } final && Matches(session, transform, block)) Put(transform, block, final);
            return;
        }
        if (live?.transformation?.Pointer != op.Pointer || context == null) return;
        if (session != null) session.Final = null; // native ApplyTranslation just restored this frame's raw move
        if (Plugin.PredictiveBuild?.Value != true || Blocked() || op.inputOverride || op.transformState == TransformState.Cancel)
        { if (session != null) session.Previous = null; return; }
        var editor = Hotkeys.Current;
        if (editor == null || editor.meshEditor.Pointer != context.editor?.Pointer
            || editor.meshEditor.Mesh?.EditMesh?.Pointer != transform.mesh.Pointer) return;
        var camera = context.Observer;
        if (camera == null) return;
        if (InputShield.Claimed || Ui.PointerOverGameUi())
        { if (session != null) session.Previous = null; return; }

        var projection = Matrix4x4.identity;
        context.GetPerspectiveMatrix(ref projection);
        projection *= block.localToWorld;
        var view = context.ViewMatrix * block.localToWorld;
        var size = context.ScreenSize;
        float near = Math.Max(1e-5f, camera.nearClipPlane), far = camera.farClipPlane;
        BuildPrediction.Projection Project(Num p)
        {
            float z = -(view.m20*p.X + view.m21*p.Y + view.m22*p.Z + view.m23);
            float w = projection.m30*p.X + projection.m31*p.Y + projection.m32*p.Z + projection.m33;
            if (z < near || z > far || !float.IsFinite(w) || Math.Abs(w) < 1e-12f) return new(new(float.NaN), -1);
            float x = projection.m00*p.X + projection.m01*p.Y + projection.m02*p.Z + projection.m03;
            float y = projection.m10*p.X + projection.m11*p.Y + projection.m12*p.Z + projection.m13;
            return new(new((x/w+1)*.5f*size.x, (y/w+1)*.5f*size.y), z);
        }
        if (session == null)
        {
            session = Capture(transform, op, block, Project, editor.meshEditor.SelectType);
            if (session == null) return;
            sessions[op.Pointer] = session;
        }
        if (!Matches(session, transform, block)) { session.Previous = null; return; }
        var elements = block.elements;
        var raw = elements.Select(p => ToNum(p.location)).ToArray();
        var anchor = session.AnchorSlot >= 0 ? raw[session.AnchorSlot] : raw.Aggregate(Num.Zero, (a,p) => a+p) / raw.Length;
        var projected = Project(anchor);
        if (projected.Depth <= 0 || !float.IsFinite(projected.Pixel.X) || !float.IsFinite(projected.Pixel.Y)) return;
        float radius = Plugin.PredictionRadius?.Value ?? 14;
        radius = float.IsFinite(radius) ? Math.Clamp(radius, 4, 40) : 14;
        // Bound depth changes as well as pixel distance: points projected on top
        // of each other must not pull the selection far into or out of the screen.
        var world = block.localToWorld.MultiplyPoint3x4(ToUnity(anchor));
        var screen = camera.WorldToScreenPoint(world);
        var dx = block.worldToLocal.MultiplyVector(camera.ScreenToWorldPoint(screen + Vector3.right * radius) - world);
        var dy = block.worldToLocal.MultiplyVector(camera.ScreenToWorldPoint(screen + Vector3.up * radius) - world);
        float reach = Math.Max(dx.magnitude, dy.magnitude) * 1.5f;
        Num Constrain(Num correction)
        {
            var adjusted = block.worldToLocal.MultiplyVector(op.ApplyVectorAxisConstraint(block.localToWorld.MultiplyVector(ToUnity(correction))));
            if ((session.CentreFlags & TransformOptions.MirrorCentreX) != 0) adjusted.x = 0;
            if ((session.CentreFlags & TransformOptions.MirrorCentreY) != 0) adjusted.y = 0;
            if ((session.CentreFlags & TransformOptions.MirrorCentreZ) != 0) adjusted.z = 0;
            var checkedAgain = block.worldToLocal.MultiplyVector(op.ApplyVectorAxisConstraint(block.localToWorld.MultiplyVector(adjusted)));
            return (checkedAgain-adjusted).sqrMagnitude > 1e-10f ? new Num(float.NaN) : ToNum(adjusted);
        }
        bool Visible(BuildPrediction.Hit hit)
        {
            if (editor.operations.XRayEnabled) return true;
            var pixel = Project(hit.TargetPosition).Pixel;
            var ray = camera.ViewportPointToRay(new(pixel.X / size.x, pixel.Y / size.y, 0));
            var origin = block.worldToLocal.MultiplyPoint3x4(ray.origin);
            var direction = block.worldToLocal.MultiplyVector(ray.direction).normalized;
            float distance = Vector3.Dot(ToUnity(hit.TargetPosition)-origin, direction);
            float epsilon = Math.Max(1e-5f, Math.Abs(distance)*1e-6f);
            if (!float.IsFinite(distance) || distance < -epsilon) return false;
            if (distance > epsilon && MeshSampling.RaycastFaces(transform.mesh, origin, direction, distance-epsilon, out MeshSampling.ElementHit _, FaceMode.Both)) return false;
            if (MeshAssistSettings.CrossPartTargets?.Value == true)
                foreach(var obstacle in session.Occluders)
                {
                    var localOrigin=obstacle.ActiveToLocal.MultiplyPoint3x4(origin);
                    var localDirection=obstacle.ActiveToLocal.MultiplyVector(direction).normalized;
                    var localTarget=obstacle.ActiveToLocal.MultiplyPoint3x4(ToUnity(hit.TargetPosition));
                    float localDistance=Vector3.Dot(localTarget-localOrigin,localDirection);
                    float localEpsilon=Math.Max(1e-5f,Math.Abs(localDistance)*1e-6f);
                    if(localDistance>localEpsilon && obstacle.Bounds.IntersectRay(new Ray(localOrigin,localDirection),out float boundsDistance)
                        &&boundsDistance<localDistance-localEpsilon
                        &&MeshSampling.RaycastFaces(obstacle.Mesh,localOrigin,localDirection,localDistance-localEpsilon,out MeshSampling.ElementHit _,FaceMode.Both))return false;
                }
            return true;
        }
        var accepted = new Dictionary<BuildPrediction.Hit, Num[]>();
        bool Safe(BuildPrediction.Hit candidate)
        {
            if (!Visible(candidate)) return false;
            var corrected = raw.Select(p => p + candidate.Correction).ToArray();
            if (corrected.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))) return false;
            var moved = session.Indices.Select((id,i) => (id, corrected[i])).ToDictionary(p => p.id, p => p.Item2);
            // Native mirror sources are global vertex indices. Match its X mirror
            // before validating faces on either side; do not move live geometry yet.
            if (block.mirrors != null) foreach (var mirror in block.mirrors)
            {
                var at = moved.TryGetValue(mirror.locationSourceIndex, out var source)
                    ? source : ToNum(transform.mesh.GetVertex(mirror.locationSourceIndex).position);
                if ((mirror.flag & TransformOptions.MirrorX) != 0) at.X = -at.X;
                moved[mirror.index] = at;
            }
            MeshTools.PredictFollowers(op, moved);
            if (MeshPlans.Folds(session.Original, session.Faces, moved) != null) return false;
            accepted[candidate] = corrected;
            return true;
        }
        var activeTargets = session.Targets.Where(t => (!t.Other || MeshAssistSettings.CrossPartTargets?.Value == true) && (t.Kind == Kind.Point
            || t.Kind == Kind.Edge && Plugin.PredictEdgeMidpoints?.Value != false
            || t.Kind == Kind.Face && Plugin.PredictFaceCentres?.Value != false)).ToList();
        if(MeshAssistSettings.EdgeSurfaceTargets?.Value != false)
            foreach(var edge in session.Segments.Where(e=>!e.Other || MeshAssistSettings.CrossPartTargets?.Value==true))
                if(BuildPrediction.ClosestEdge(anchor,edge.A,edge.B,out var closest))
                    activeTargets.Add(new(new(edge.Id,closest),Kind.EdgePoint,edge.Other));
        if(MeshAssistSettings.FaceSurfaceTargets?.Value == true)
            foreach(var face in session.Triangles.Where(f=>!f.Other || MeshAssistSettings.CrossPartTargets?.Value==true))
                if(BuildPrediction.ClosestFace(anchor,face.A,face.B,face.C,out var closest))
                    activeTargets.Add(new(new(face.Id,closest),Kind.FacePoint,face.Other));
        bool cycleNext = Keybinds.Pressed("cyclePrediction") && session.LastCycleFrame != Time.frameCount;
        if(cycleNext)session.LastCycleFrame=Time.frameCount;
        var targets=activeTargets.Select(t=>t.Value);
        var hit = BuildPrediction.Resolve(targets, new(session.Anchor, anchor-session.Anchor, RadiusPixels: radius, MaxCorrection: reach),
            Project, session.Previous, Constrain, accept: Safe, preferredTargetId: session.PinnedTarget, cycleNext: cycleNext);
        if(cycleNext && hit is { } cycled) session.PinnedTarget=cycled.TargetId;
        else if(hit?.TargetId != session.PinnedTarget)session.PinnedTarget=null;
        session.Previous = hit;
        if (hit is not { } suggestion) return;
        bool held = Keybinds.Held("applyPrediction");
        var corrected = accepted[suggestion];
        if (held) { Put(transform, block, corrected); session.Final = corrected; }
        var selectedTarget = activeTargets.First(t => t.Value.Id == suggestion.TargetId);
        var kind=selectedTarget.Kind;
        string name = kind == Kind.Point ? "Point" : kind == Kind.Edge ? "Edge midpoint" : kind==Kind.Face ? "Face centre"
            : kind==Kind.EdgePoint ? "Edge point" : "Face surface";
        if(selectedTarget.Other)name="Other part · "+name.ToLowerInvariant();
        if (!suggestion.Coincident) name = "Align to " + name.ToLowerInvariant();
        guide = new(camera, world, block.localToWorld.MultiplyPoint3x4(ToUnity(suggestion.AnchorPosition)),
            block.localToWorld.MultiplyPoint3x4(ToUnity(suggestion.TargetPosition)),
            name + (held ? " · applied" : $" · hold {Keybinds.Shown("applyPrediction")}")
                + (Keybinds.Shown("cyclePrediction")!="(none)" ? $" · {Keybinds.Shown("cyclePrediction")} next" : ""), Time.frameCount, held);
    }

    static Session? Capture(MeshTransformation transform, Transformation op, TransformBlock block,
        Func<Num, BuildPrediction.Projection> project, MeshEditType mode)
    {
        var mesh = transform.mesh;
        if (mesh.vertices.Count > ushort.MaxValue || mesh.loops.Count > ushort.MaxValue) return null;
        mesh.ReIndex(ElementType.Vertex | ElementType.Edge | ElementType.Face | ElementType.Loop);
        var elements = block.elements;
        var excluded = elements.Select(p => p.index).ToHashSet();
        if (block.mirrors != null) foreach (var p in block.mirrors) excluded.Add(p.index);
        var followers = MeshTools.PredictionFollowers(op).ToArray();
        foreach (var p in followers) excluded.Add(p.Vertex.index);
        var s = new Session { Owner = op, Mesh = mesh.Pointer, Indices = elements.Select(p => p.index).ToArray(),
            Original = mesh.VertexPositions.Select(ToNum).ToArray() };
        foreach (var p in elements) { s.Original[p.index] = ToNum(p.initialLocation); s.CentreFlags |= p.flag; }
        if (block.mirrors != null) foreach (var p in block.mirrors) s.Original[p.index] = ToNum(p.initialLocation);
        foreach (var p in followers) s.Original[p.Vertex.index] = p.Original;
        if (mode == MeshEditType.Vertex)
        {
            var pointer = new N2(op.initialPointerPos.x, op.initialPointerPos.y);
            float closest = float.PositiveInfinity;
            for (int i=0;i<elements.Length;i++)
            {
                var screen = project(ToNum(elements[i].initialLocation));
                float distance = N2.DistanceSquared(screen.Pixel, pointer);
                if (screen.Depth > 0 && distance < closest) { closest = distance; s.AnchorSlot = i; }
            }
        }
        s.Anchor = s.AnchorSlot >= 0 ? ToNum(elements[s.AnchorSlot].initialLocation)
            : elements.Select(p => ToNum(p.initialLocation)).Aggregate(Num.Zero, (a,p) => a+p) / elements.Length;
        int nextSurfaceId=int.MinValue;
        for (int i=0;i<s.Original.Length;i++) if (!excluded.Contains(i)) s.Targets.Add(new(new(i, s.Original[i]), Kind.Point));
        for (int i=0;i<mesh.edges.Count;i++)
        {
            var e = mesh.edges[i]; int a=e.v0.index,b=e.v1.index;
            if (!excluded.Contains(a) && !excluded.Contains(b))
            {
                s.Targets.Add(new(new(-i-1, (s.Original[a]+s.Original[b])*.5f), Kind.Edge));
                s.Segments.Add(new(nextSurfaceId++,s.Original[a],s.Original[b]));
            }
        }
        for (int i=0;i<mesh.faces.Count;i++)
        {
            var ids = HoleQuality.Corners(mesh.faces[i], v => (int)v.index);
            if (ids.Any(excluded.Contains)) s.Faces.Add(ids);
            if (ids.All(v => !excluded.Contains(v)))
            {
                s.Targets.Add(new(new(-mesh.edges.Count-i-1,
                    ids.Aggregate(Num.Zero, (a,v) => a+s.Original[v])/ids.Length), Kind.Face));
                for(int k=1;k+1<ids.Length;k++)s.Triangles.Add(new(nextSurfaceId++,s.Original[ids[0]],s.Original[ids[k]],s.Original[ids[k+1]]));
            }
        }
        if(MeshAssistSettings.CrossPartTargets?.Value==true && !ExplodedView.Active) CaptureOtherParts(s,mesh,block,ref nextSurfaceId);
        return s;
    }

    static void CaptureOtherParts(Session session, EditMesh activeMesh, TransformBlock block, ref int nextId)
    {
        const int MaximumOtherElements=75000;
        int remaining=MaximumOtherElements;
        bool skipped=false;
        var current=Hotkeys.Current?.Component?.VehicleObject;
        foreach(var part in DesignEditor.Instance?.AllParts() ?? Enumerable.Empty<Sprocket.Vehicles.VehicleObject>())
        {
            if(part.Pointer==current?.Pointer)continue;
            var structure=DesignEditor.Each(part.Components).Select(c=>c?.TryCast<PlateStructure>()).FirstOrDefault(s=>s?.Mesh?.EditMesh!=null);
            if(structure?.Mesh?.EditMesh is not { } mesh || mesh.Pointer==activeMesh.Pointer || mesh.vertices.Count==0)continue;
            if(mesh.vertices.Count>ushort.MaxValue)continue;
            var origin=block.worldToLocal.MultiplyPoint3x4(structure.StructureToWorldSpace(Vector3.zero));
            var x=block.worldToLocal.MultiplyPoint3x4(structure.StructureToWorldSpace(Vector3.right))-origin;
            var y=block.worldToLocal.MultiplyPoint3x4(structure.StructureToWorldSpace(Vector3.up))-origin;
            var z=block.worldToLocal.MultiplyPoint3x4(structure.StructureToWorldSpace(Vector3.forward))-origin;
            var frame=new Matrix4x4(new(x.x,x.y,x.z,0),new(y.x,y.y,y.z,0),new(z.x,z.y,z.z,0),new(origin.x,origin.y,origin.z,1));
            if(!float.IsFinite(frame.determinant)||Math.Abs(frame.determinant)<1e-10f)continue;
            // Keep every plate as an occluder even when the suggestion budget is
            // exhausted, so budget limits cannot expose targets through a wall.
            session.Occluders.Add(new(mesh,frame.inverse,structure.Mesh.LocalBounds));
            if(mesh.vertices.Count>remaining){skipped=true;continue;}
            var vertices=mesh.vertices.ToArray();
            var index=vertices.Select((v,i)=>(v.Pointer,i)).ToDictionary(p=>p.Pointer,p=>p.i);
            var positions=vertices.Select(v=>ToNum(frame.MultiplyPoint3x4(v.position))).ToArray();
            if(positions.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)))continue;
            foreach(var at in positions) { session.Targets.Add(new(new(nextId++,at),Kind.Point,true)); remaining--; }
            foreach(var edge in mesh.edges)
            {
                if(remaining<2){skipped=true;break;}
                if(!index.TryGetValue(edge.v0.Pointer,out int a)||!index.TryGetValue(edge.v1.Pointer,out int b))continue;
                session.Targets.Add(new(new(nextId++,(positions[a]+positions[b])*.5f),Kind.Edge,true));
                session.Segments.Add(new(nextId++,positions[a],positions[b],true)); remaining-=2;
            }
            foreach(var face in mesh.faces)
            {
                if(remaining<3){skipped=true;break;}
                var ids=HoleQuality.Corners(face,v=>index[v.Pointer]);
                if(ids.Length is <3 or >4)continue;
                session.Targets.Add(new(new(nextId++,ids.Aggregate(Num.Zero,(a,v)=>a+positions[v])/ids.Length),Kind.Face,true));remaining--;
                for(int k=1;k+1<ids.Length;k++) {session.Triangles.Add(new(nextId++,positions[ids[0]],positions[ids[k]],positions[ids[k+1]],true));remaining--;}
            }
        }
        if(skipped)Plugin.ModLog.LogInfo("Build assist: this move reached the other-part suggestion budget. Some detailed targets were omitted; their plates still block hidden targets.");
    }

    static bool Matches(Session s, MeshTransformation t, TransformBlock b) => s.Mesh == t.mesh.Pointer
        && s.Indices.Length == b.elements.Length && s.Indices.Where((id,i) => id != b.elements[i].index).Any() == false;
    static void Put(MeshTransformation transform, TransformBlock block, IReadOnlyList<Num> positions)
    {
        var elements = block.elements;
        for (int i=0;i<elements.Length;i++)
        {
            var p=elements[i]; var at=ToUnity(positions[i]);
            if ((p.flag & TransformOptions.MirrorCentreX) != 0) at.x=0;
            if ((p.flag & TransformOptions.MirrorCentreY) != 0) at.y=0;
            if ((p.flag & TransformOptions.MirrorCentreZ) != 0) at.z=0;
            p.location=at; elements[i]=p; transform.mesh.GetVertex(p.index).position=at;
        }
        transform.ApplyMirrorPositions(block);
        transform.mesh.MarkDirty(MeshDirtyFlags.Geometry);
    }

    internal static void LeftEditor()
    {
        sessions.Clear(); history.Clear(); live=null; context=null; replay=IntPtr.Zero; guide=null;
    }
    static Num ToNum(Vector3 p) => new(p.x,p.y,p.z);
    static Vector3 ToUnity(Num p) => new(p.X,p.Y,p.Z);

    internal static void Draw()
    {
        if (guide is not { } g || Time.frameCount-g.Frame > 1 || Plugin.PredictiveBuild?.Value != true
            || Blocked() || Event.current.type != EventType.Repaint) return;
        var a=g.Camera.WorldToScreenPoint(g.Source); var b=g.Camera.WorldToScreenPoint(g.Destination);
        var target=g.Camera.WorldToScreenPoint(g.Target);
        if (a.z <= g.Camera.nearClipPlane || b.z <= g.Camera.nearClipPlane || target.z <= g.Camera.nearClipPlane) return;
        // GL lines (GeometryPreview), in screen pixels from the bottom: the game's build stripped GUI.DrawTexture and
        // GUIUtility.RotateAroundPivot.
        Vector2 from=new(a.x,a.y), to=new(b.x,b.y), marker=new(target.x,target.y);
        Vector2 c1=marker+new Vector2(-6,-6), c2=marker+new Vector2(6,-6), c3=marker+new Vector2(6,6), c4=marker+new Vector2(-6,6);
        GeometryPreview.DrawScreenLines(new[] { from,to, to,marker, c1,c2, c2,c3, c3,c4, c4,c1 },
            g.Applied ? new Color(.35f,1,.55f) : new Color(1,.8f,.25f), 2);
        label ??= new GUIStyle(GUI.skin.box) { fontSize=13, alignment=TextAnchor.MiddleLeft };
        GUI.Box(new Rect(Math.Clamp(marker.x+12,4,Math.Max(4,Screen.width-284)),
            Math.Clamp(Screen.height-marker.y+10,4,Math.Max(4,Screen.height-30)),280,26),g.Text,label);
    }
}
