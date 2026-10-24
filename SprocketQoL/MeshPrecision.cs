using Sprocket.PlateMesh;
using Sprocket.Vehicles.PlateStructures.Design;
using UnityEngine;
using Num = System.Numerics.Vector3;

namespace SprocketQoL;

public static partial class MeshTools
{
    static int loopCuts = 1;
    static float loopOffset = 50, slideAmount = 25, exactLengthMm = 100;
    static bool slideOpposite, slideMillimetres, selectionMeasures = true;
    static bool proportionalConnected = true, proportionalTopology;
    static PrecisionGeometry.FalloffShape proportionalFalloff;
    static PrecisionGeometry.Health? health;
    static IntPtr healthMesh;
    static string selectionReport = "Select two points to measure their distance, edges for total length, or two faces for their angle.";
    static float nextSelectionMeasure;
    static Num? alignmentTarget;
    static IntPtr alignmentVehicle;

    static Num? Direction(View view,Func<Num,Num> world)
    {
        if(view.SelectedFaces.Count==1)return HoleRing.Normal(view.Corners[view.SelectedFaces.First()].Select(i=>world(view.Pos[i])).ToArray());
        var points=Chosen(view).OrderBy(i=>i).ToArray();
        return points.Length==2?world(view.Pos[points[1]])-world(view.Pos[points[0]]):null;
    }
    static void StoreAlignment(PlateStructureEditor editor)
    {
        if(EditorModal.BlocksInput) {editor.operations.NotifyError("Close the open QoL window before changing the reference.");return;}
        if(editor.meshEditor.Mesh?.EditMesh is not {} mesh||!MeshSelection.TryMeshFrame(editor,out var matrix))return;
        Num World(Num p) {var w=matrix.MultiplyPoint3x4(new(p.X,p.Y,p.Z));return new(w.x,w.y,w.z);}
        var direction=Direction(new View(mesh),World);
        if(direction is not {} d||d.LengthSquared()<1e-12f){editor.operations.NotifyError("Select one face or two points to store its world direction.");return;}
        alignmentTarget=Num.Normalize(d);alignmentVehicle=DesignEditor.Instance!.Core!.Target.Pointer;editor.RequestRedraw();
    }
    static void AlignSelection(PlateStructureEditor editor)
    {
        if(alignmentTarget is not {} target||alignmentVehicle!=DesignEditor.Instance?.Core?.Target?.Pointer||!MeshSelection.TryMeshFrame(editor,out var matrix))
        {editor.operations.NotifyError("Store a reference edge or face on this vehicle first.");return;}
        bool mirror=editor.meshEditor.Symmetry;var inverse=matrix.inverse;
        Num World(Num p){var w=matrix.MultiplyPoint3x4(new(p.X,p.Y,p.Z));return new(w.x,w.y,w.z);}
        Num Local(Num p){var w=inverse.MultiplyPoint3x4(new(p.X,p.Y,p.Z));return new(w.x,w.y,w.z);}
        Run(editor,"Align selection",mesh=>
        {
            var view=new View(mesh);var direction=Direction(view,World);
            if(direction is not {} from)return(false,"select one face or exactly two points to align");
            var moved=PrecisionGeometry.Align(view.Pos.Select(World).ToArray(),Chosen(view),from,target).ToDictionary(p=>p.Key,p=>Local(p.Value));
            return MovePoints(mesh,view,moved,mirror,"aligned parallel to stored reference");
        });
    }

    internal static IEnumerable<RightClickMenu.Command> PrecisionCommands()
    {
        var editor=Hotkeys.Current;
        foreach(string name in new[]{"Slide selection","Set selection length","Weld selected duplicates","Align selection"})
        {
            string tool=name;
            yield return new("precision/"+tool,"Precision",tool,"",editor!=null,"Select a structure in mesh mode",()=>{if(editor!=null)InvokeTool(editor,tool);});
        }
        yield return new("precision/health","Precision","Inspect mesh health","",editor!=null,"Select a structure in mesh mode",()=>{if(editor!=null)InspectMesh(editor);});
        yield return new("precision/reference","Precision","Store alignment reference","",editor!=null,"Select a structure in mesh mode",()=>{if(editor!=null)StoreAlignment(editor);});
    }

    static void InvokeTool(PlateStructureEditor editor, string name)
    {
        switch (name)
        {
            case "Flatten": Flatten(editor); break;
            case "Loop cut": LoopCut(editor); break;
            case "Inset": Inset(editor); break;
            case "Bevel": Bevel(editor); break;
            case "Smooth Edge": SmoothEdge(editor); break;
            case "Fillet": Fillet(editor); break;
            case "Bridge": Bridge(editor); break;
            case "Circle": Circle(editor); break;
            case "Split Face": SplitEdges(editor); break;
            case "Slide selection": SlideSelection(editor); break;
            case "Set selection length": SetSelectionLength(editor); break;
            case "Weld selected duplicates": WeldSelectedDuplicates(editor); break;
            case "Align selection": AlignSelection(editor); break;
            default: editor.operations.NotifyError("This tool cannot be adjusted."); break;
        }
    }

    internal static void RegisterPrecisionSettings()
    {
        ToolSettings.RegisterFields(typeof(MeshTools), "Mesh tools", "flattenMode", "insetMm", "bevelMm", "flatAngle", "radiusMm",
            "smoothMm", "filletRadiusMm", "smoothSegments", "filletSegments", "splitSections", "filletAutoControls", "filletControlMm",
            "splitOtherDirection", "proportional", "halfGrid", "bridgeCuts", "bridgeSmooth", "mirrorMm", "mirrorKeep",
            "loopCuts", "loopOffset", "slideAmount", "exactLengthMm", "slideOpposite", "slideMillimetres", "selectionMeasures",
            "proportionalConnected", "proportionalTopology", "proportionalFalloff");
    }
    /// The selected points (with those of selected edges and faces), edges and faces' corners.
    internal sealed record Picked(List<Vertex> Points, List<(Vertex A, Vertex B)> Edges, List<Vertex[]> Faces);
    static (IntPtr Mesh, float Until, Picked Picked) chosenNow;
    static (IntPtr Mesh, float Until, long Hash) fingerprintNow;

    /// What's selected, read from the selection flags at most 10 times a second: the measurements and their overlay
    /// built a whole View for it (every frame, and 4 times a second), milliseconds each time on big shapes. Positions are
    /// read fresh by the caller, so moved points still follow at once.
    internal static Picked Selected(EditMesh mesh)
    {
        if (chosenNow.Mesh == mesh.Pointer && Time.unscaledTime < chosenNow.Until) return chosenNow.Picked;
        var seen = new HashSet<IntPtr>();
        var picked = new Picked(new(), new(), new());
        void Add(Vertex p) { if (seen.Add(p.Pointer)) picked.Points.Add(p); }
        var vertices = mesh.vertices;
        for (int i = 0; i < vertices.Count; i++) if (vertices[i].HasFlag(ElementFlags.Selected)) Add(vertices[i]);
        var edges = mesh.edges;
        for (int i = 0; i < edges.Count; i++) if (edges[i].HasFlag(ElementFlags.Selected)) { picked.Edges.Add((edges[i].v0, edges[i].v1)); Add(edges[i].v0); Add(edges[i].v1); }
        var faces = mesh.faces;
        for (int i = 0; i < faces.Count; i++)
            if (faces[i].HasFlag(ElementFlags.Selected)) { var corners = HoleQuality.Corners(faces[i], p => p); picked.Faces.Add(corners); foreach (var corner in corners) Add(corner); }
        chosenNow = (mesh.Pointer, Time.unscaledTime + 0.1f, picked);
        return picked;
    }

    /// A number that changes when the selection or where its points are does (read at most 10 times a second), for
    /// previews that would otherwise show an old answer while the counts stay the same.
    internal static long SelectionFingerprint(EditMesh mesh)
    {
        if (fingerprintNow.Mesh == mesh.Pointer && Time.unscaledTime < fingerprintNow.Until) return fingerprintNow.Hash;
        var picked = Selected(mesh);
        var hash = new HashCode();
        foreach (var p in picked.Points) { hash.Add(p.Pointer); hash.Add(p.position); }
        hash.Add(picked.Edges.Count); hash.Add(picked.Faces.Count);
        fingerprintNow = (mesh.Pointer, Time.unscaledTime + 0.1f, hash.ToHashCode());
        return fingerprintNow.Hash;
    }

    static HashSet<int> Chosen(View v)
    {
        var points=new HashSet<int>(v.SelectedPoints);
        foreach(int f in v.SelectedFaces) points.UnionWith(v.Corners[f]);
        foreach(var edge in v.SelectedEdges) { points.Add(edge.A);points.Add(edge.B); }
        return points;
    }
    static (bool,string) MovePoints(EditMesh mesh,View v,Dictionary<int,Num> moved,bool mirror,string name)
    {
        if(moved.Count==0) return (false,"no safe movement: choose points with a neighbouring unselected edge");
        if(mirror) foreach(var (source,twin) in MeshPlans.Twins(v.Pos,moved.Keys.ToArray(),Tolerance))
            if(!moved.ContainsKey(twin)) moved[twin]=new Num(-moved[source].X,moved[source].Y,moved[source].Z);
        if(MeshPlans.Folds(v.Pos,v.Corners,moved) is { } why) return (false,why);
        foreach(var (id,p) in moved) v.Verts[id].position=new(p.X,p.Y,p.Z);
        mesh.MarkDirty(MeshDirtyFlags.Geometry);
        return (true,$"{name}: {moved.Count} points");
    }
    internal static void SlideSelection(PlateStructureEditor editor)
    {
        bool mirror=editor.meshEditor.Symmetry,opposite=slideOpposite,mm=slideMillimetres;float amount=slideAmount;
        bool hasFrame=MeshSelection.TryMeshFrame(editor,out var frame);
        Num World(Num p) {var w=frame.MultiplyPoint3x4(new(p.X,p.Y,p.Z));return new(w.x,w.y,w.z);}
        Run(editor,"Slide selection",mesh=>
        {
            var v=new View(mesh);return MovePoints(mesh,v,PrecisionGeometry.Slide(v.Pos,v.Corners,Chosen(v),amount,mm,opposite,hasFrame?World:null),mirror,"slid");
        });
    }
    internal static void SetSelectionLength(PlateStructureEditor editor)
    {
        bool mirror=editor.meshEditor.Symmetry;float metres=exactLengthMm/1000;
        bool hasFrame=MeshSelection.TryMeshFrame(editor,out var frame);
        Run(editor,"Set selection length",mesh=>
        {
            var v=new View(mesh);var points=Chosen(v).OrderBy(value => v.Verts[value].index).ToArray();
            if(points.Length!=2) return(false,"select exactly two points; the first index stays fixed");
            var direction=v.Pos[points[1]]-v.Pos[points[0]];
            float localLength=direction.Length(),worldLength=hasFrame?frame.MultiplyVector(new(direction.X,direction.Y,direction.Z)).magnitude:localLength;
            if(worldLength<1e-10f)return(false,"the selected points have no usable separation");
            return MovePoints(mesh,v,PrecisionGeometry.SetDistance(v.Pos,points[0],points[1],metres*localLength/worldLength),mirror,"set exact distance");
        });
    }
    internal static void InspectMesh(PlateStructureEditor editor)
    {
        if(editor.meshEditor.Mesh?.EditMesh is not { } mesh) return;
        var v=new View(mesh);health=PrecisionGeometry.Inspect(v.Pos,v.Corners);healthMesh=mesh.Pointer;editor.RequestRedraw();
    }
    static void SelectMeshProblems(PlateStructureEditor editor)
    {
        if(editor.meshEditor.Mesh?.EditMesh is not { } mesh) return;
        InspectMesh(editor);if(health==null) return;
        var v=new View(mesh);
        var problems=health.ProblemVertices.Concat(health.ProblemFaces.SelectMany(i=>v.Corners[i])).Distinct().Select(i=>v.Verts[i]).ToList();
        // One of the game's selection steps: Ctrl+Z gives back the selection from before.
        MeshSelection.SelectAsStep(editor,"Select problems",Sprocket.MeshEditing.MeshEditType.Vertex,_=>{foreach(var p in problems)p.EnableFlag(ElementFlags.Selected);});
        editor.RequestRedraw();
    }
    static void WeldSelectedDuplicates(PlateStructureEditor editor)
    {
        bool mirror = editor.meshEditor.Symmetry;
        Run(editor,"Weld selected duplicates",mesh=>
        {
            var view = new View(mesh); var selected = Chosen(view);
            if (mirror) selected.UnionWith(MeshPlans.Twins(view.Pos, selected.ToArray(), Tolerance).Values);
            var plan = PrecisionGeometry.WeldDuplicates(view.Pos, view.Corners, selected);
            var corners = view.Faces.Select(f =>
            {
                var values = new List<(ushort Thickness,ThickenMode Mode)>(); var loop = f.firstLoop;
                for (int i = 0; i < f.vertexCount; i++, loop = loop.next) values.Add((loop.thickness,loop.thickenMode));
                return values.ToArray();
            }).ToArray();
            return Apply(mesh, view, plan, "welded selected duplicate points", gaps:false, preserveBevelEdges:true,
                afterApply: made =>
                {
                    foreach (var (face, source) in made)
                    {
                        var current = face.firstLoop;
                        for (int i = 0; i < face.vertexCount; i++, current = current.next)
                        {
                            current.thickness = corners[source][i].Thickness;
                            current.thickenMode = corners[source][i].Mode == ThickenMode.AlongEdgeManual ? ThickenMode.AlongEdgeAuto : corners[source][i].Mode;
                        }
                    }
                });
        });
    }
    static void DrawPrecision(PlateStructureEditor editor,Panel layout,InspectorUi ui)
    {
        Ui.Section(layout,"Precision and measurements");
        ui.ToggleField("Measurements",selectionMeasures,Ui.BoolCallback(v=>selectionMeasures=v),"Selected point distance, total edge length and angle between two faces. Overlay uses the edited part's native transform.");
        ui.InfoField(selectionReport,5);
        ui.Slider("Target length (mm)",exactLengthMm,1,10000,Ui.FloatCallback(v=>exactLengthMm=MathF.Round(v)));
        ui.Button("Set two-point length",Ui.Callback(()=>SetSelectionLength(editor)),new("Exact distance","Select exactly two points. The point with the lower mesh index stays fixed. The other moves along their current line. Mirror and Ctrl+Z apply."));
        ui.ToggleField("Slide in mm",slideMillimetres,Ui.BoolCallback(v=> {slideMillimetres=v;slideAmount=v?10:25;editor.RequestRedraw();}),"Off: slide a percentage along the edge. On: move this many millimetres along the edge.");
        ui.Slider(slideMillimetres?"Slide (mm)":"Slide (%)",slideAmount,1,slideMillimetres?500:95,Ui.FloatCallback(v=>slideAmount=v));
        ui.Button(slideOpposite?"Slide direction: B":"Slide direction: A",Ui.Callback(()=> {slideOpposite=!slideOpposite;editor.RequestRedraw();}),new("Slide direction","Chooses the other neighbouring edge from the first selected point. All selected points slide toward matching rails; a collapsed or folded result is rejected."));
        ui.Button("Slide selected points/edges",Ui.Callback(()=>SlideSelection(editor)),new("Slide","Moves selected points along connected edges toward unselected neighbouring points. Use direction A/B to choose the side. Does not create points or change the face outline beyond this movement; Mirror and undo apply."));
        ui.Button("Store edge / face reference",Ui.Callback(()=>StoreAlignment(editor)),new("Alignment reference","Select one face or two points. Stores its world direction on this vehicle; then select the geometry to align, including on another part."));
        ui.Button("Rotate parallel to reference",Ui.Callback(()=>AlignSelection(editor)),new("Parallel alignment","Rotate a selected face or edge about its centre to match the stored reference. Hover to see the result; Ctrl+Z undoes it. Folded or crossed faces are refused."));

        Ui.Section(layout,"Proportional influence");
        ui.ToggleField("Connected only",proportionalConnected,Ui.BoolCallback(v=>proportionalConnected=v),"Keep nearby but disconnected pieces from following a move. Applies to the next move.");
        ui.ToggleField("Along topology",proportionalTopology,Ui.BoolCallback(v=>proportionalTopology=v),"Measure influence along connected edges instead of straight through space.");
        ui.Button("Falloff: "+proportionalFalloff,Ui.Callback(()=> {proportionalFalloff=(PrecisionGeometry.FalloffShape)(((int)proportionalFalloff+1)%4);editor.RequestRedraw();}),new("Falloff","Smooth, Linear, Sharp or Constant influence. Applies to the next transform."));

        Ui.Section(layout,"Mesh health");
        ui.Button("Inspect this mesh",Ui.Callback(()=>InspectMesh(editor)),new("Mesh health","Reports open borders, non-manifold edges, duplicate points, collapsed faces and invalid faces. Open borders can be intentional. Inspection does not change the shape."));
        if(health is { } h&&healthMesh==editor.meshEditor.Mesh?.EditMesh?.Pointer)
        {
            ui.InfoField($"{h.Vertices} points · {h.Edges} edges · {h.Faces} faces\n{h.OpenEdges} open edges · {h.NonManifoldEdges} non-manifold\n{h.DuplicatePoints} duplicate points · {h.DegenerateFaces} collapsed · {h.InvalidFaces} invalid faces",4);
            ui.Button("Select reported points",Ui.Callback(()=>SelectMeshProblems(editor)),new("Select problems","Selects the reported duplicate/invalid/non-manifold points. Review them before using the native Merge or Delete commands."));
        }
        ui.Button("Weld selected duplicate points",Ui.Callback(()=>WeldSelectedDuplicates(editor)),new("Weld duplicates","Select the seam points to join. Only selected duplicates within 0.01 mm are welded; collapsed or non-manifold results are refused. Hover to see the result; Ctrl+Z undoes it."));    }
    internal static void TickPrecision()
    {
        if(!selectionMeasures||Time.unscaledTime<nextSelectionMeasure||Hotkeys.Current is not { } e||e.meshEditor.Mesh?.EditMesh is not { } mesh) return;
        nextSelectionMeasure=Time.unscaledTime+.25f;
        // Only the selected elements are read (review WF-05: this read and stored the whole shape 4 times a second).
        var picked=Selected(mesh);var points=picked.Points;
        if(points.Count==0) { selectionReport="Select two points for distance, edges for length, or two faces for angle.";return; }
        bool frame=MeshSelection.TryMeshFrame(e,out var matrix);
        Num At(Vertex vertex) { var p=vertex.position;if(!frame)return new(p.x,p.y,p.z);var w=matrix.MultiplyPoint3x4(p);return new(w.x,w.y,w.z); }
        var measured=points.Select(At).ToArray();var min=measured.Aggregate(Num.Min);var max=measured.Aggregate(Num.Max);var size=max-min;
        var lines=new List<string> { $"{points.Count} points · size {size.X*1000:0.##} × {size.Y*1000:0.##} × {size.Z*1000:0.##} mm" };
        if(points.Count==2) lines.Add($"Distance: {Num.Distance(measured[0],measured[1])*1000:0.###} mm");
        if(picked.Edges.Count>0) lines.Add($"Selected edges: {picked.Edges.Sum(edge=>Num.Distance(At(edge.A),At(edge.B)))*1000:0.###} mm total");
        if(picked.Faces.Count==2)
        {
            var faces=picked.Faces;var ns=faces.Select(f=>HoleRing.Normal(f.Select(At).ToArray())).ToArray();
            if(ns.All(n=>n.LengthSquared()>1e-12f)) lines.Add($"Face angle: {MathF.Acos(Math.Clamp(Num.Dot(Num.Normalize(ns[0]),Num.Normalize(ns[1])),-1,1))*180/MathF.PI:0.###}°");
            var centres=faces.Select(f=>f.Select(At).Aggregate(Num.Zero,(a,b)=>a+b)/f.Length).ToArray();
            lines.Add($"Face centres: {Num.Distance(centres[0],centres[1])*1000:0.###} mm apart");
            float clearance=PrecisionGeometry.Clearance(faces[0].Select(At).ToArray(),faces[1].Select(At).ToArray());
            if(float.IsFinite(clearance))lines.Add($"Face clearance: {clearance*1000:0.###} mm");
        }
        if(!frame) lines.Add("Local mesh units; move pointer over the mesh for world measurements.");
        string report=string.Join("\n",lines);
        if(selectionReport!=report) {selectionReport=report;e.RequestRedraw();}
    }
    internal static void DrawSelectionMeasures()
    {
        if((!selectionMeasures&&!proportional)||DrawingSheet.Capturing||PhotoShot.Capturing||EditorModal.BlocksInput||Hotkeys.Current is not { } e||e.meshEditor.Mesh?.EditMesh is not { } mesh
            ||!MeshSelection.TryMeshFrame(e,out var matrix)||Camera.main is not { } camera||Event.current.type!=EventType.Repaint) return;
        var points=Selected(mesh).Points;
        if(proportional&&points.Count>0)
        {
            var centre=points.Aggregate(Num.Zero,(a,p)=>a+HoleQuality.ToNum(p.position))/points.Count;
            var ring=new List<Vector3>();var edges=new List<int[]>();float radius=radiusMm/1000;
            foreach(int plane in new[]{0,1,2})for(int k=0;k<64;k++)
            {
                float t=k*MathF.Tau/64;var offset=plane==0?new Num(MathF.Cos(t),MathF.Sin(t),0):plane==1?new Num(MathF.Cos(t),0,MathF.Sin(t)):new Num(0,MathF.Cos(t),MathF.Sin(t));
                var p=centre+radius*offset;ring.Add(new(p.X,p.Y,p.Z));edges.Add(new[]{plane*64+k,plane*64+(k+1)%64});
            }
            GeometryPreview.DrawWire(camera,ring,edges,matrix,new Color(1,.75f,.2f,.55f));
        }
        if(selectionMeasures&&points.Count==2)
        {
            var a=matrix.MultiplyPoint3x4(points[0].position);var b=matrix.MultiplyPoint3x4(points[1].position);
            GeometryPreview.DrawWire(camera,new[]{a,b},new[]{new[]{0,1}},Matrix4x4.identity,new Color(.2f,1,.9f));
            var at=camera.WorldToScreenPoint((a+b)/2);
            if(at.z>camera.nearClipPlane) GUI.Box(new Rect(Math.Clamp(at.x+8,0,Math.Max(0,Screen.width-225)),Math.Clamp(Screen.height-at.y+8,0,Math.Max(0,Screen.height-26)),220,24),$"{Vector3.Distance(a,b)*1000:0.###} mm");
        }
    }
}
