using System.Numerics;
using SprocketQoL;

static class MeshAssistTests
{
    static int checks;
    static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    static void Near(Vector3 a,Vector3 b,string message,float epsilon=1e-5f)=>Check(Vector3.Distance(a,b)<epsilon,$"{message}: {a} != {b}");
    internal static void Run()
    {
        checks=0;
        var edges=new[]{(0,1),(1,2),(2,3),(3,0)};var faces=new[]{new[]{0,1,2,3}};
        var identities=new long[]{101,102,103,104};
        string topology=SelectionMemory.Fingerprint(4,edges,faces,identities);
        Check(topology==SelectionMemory.Fingerprint(4,edges,faces,identities),"unchanged mesh fingerprint is stable");
        Check(topology!=SelectionMemory.Fingerprint(4,new[]{(0,2),(1,2),(2,3),(3,0)},faces,identities),"same counts with changed connectivity invalidate");
        Check(topology!=SelectionMemory.Fingerprint(4,edges,new[]{new[]{0,3,2,1}},identities),"changed face winding invalidates");
        Check(topology!=SelectionMemory.Fingerprint(4,edges,faces,new long[]{102,101,103,104}),"reindexed element identities invalidate even symmetric topology");
        Check(topology!=SelectionMemory.Fingerprint(4,edges,faces,new long[]{201,202,203,204}),"a replacement mesh is not confused with matching index counts");
        var memory=new SelectionMemory();var scope=new SelectionMemory.Scope("loaded-design-1",20,topology);
        Check(memory.Save("Turret roof",scope,2,new[]{3,1,1},new[]{2},new[]{0},out _),"named selection can be saved");
        var saved=memory.Recall("Turret roof",scope,4,4,1,out _)!;
        Check(saved.Mode==2&&saved.Points.SequenceEqual(new[]{1,3}),"mode is saved and indices are deduplicated deterministically");
        saved.Points[0]=99;
        Check(memory.Recall("Turret roof",scope,4,4,1,out _)!.Points[0]==1,"recall returns defensive arrays");
        Check(memory.Recall("Turret roof",scope with{Design="loaded-design-2"},4,4,1,out _)==null,"another design cannot recall this selection");
        Check(memory.Recall("Turret roof",scope with{Part=21},4,4,1,out _)==null,"another part cannot recall this selection");
        Check(memory.Recall("Turret roof",scope with{Topology="changed"},4,4,1,out _)==null,"topology edits are rejected safely");
        Check(memory.Recall("Turret roof",scope,3,4,1,out _)==null,"invalidated index bounds are rejected even if a caller gives an old signature");
        Check(memory.Save("Turret roof",scope,1,new[]{0},Array.Empty<int>(),Array.Empty<int>(),out _),"existing named set can be overwritten intentionally");
        Check(!memory.Save("",scope,1,new[]{0},Array.Empty<int>(),Array.Empty<int>(),out _),"empty names are rejected");
        Check(!memory.Save("a\nb",scope,1,new[]{0},Array.Empty<int>(),Array.Empty<int>(),out _),"control characters in names are rejected");
        Check(!memory.Save("Empty",scope,1,Array.Empty<int>(),Array.Empty<int>(),Array.Empty<int>(),out _),"empty selections cannot be saved");
        Check(!memory.Save("Negative",scope,1,new[]{-1},Array.Empty<int>(),Array.Empty<int>(),out _),"negative indices cannot be saved");
        for(int i=1;i<SelectionMemory.MaximumPerPart;i++)Check(memory.Save("Set "+i,scope,0,new[]{0},Array.Empty<int>(),Array.Empty<int>(),out _),"bounded named sets within limit");
        Check(!memory.Save("Too many",scope,0,new[]{0},Array.Empty<int>(),Array.Empty<int>(),out _),"named sets have a per-part memory bound");
        Check(memory.Save("Another part",scope with{Part=21},0,new[]{0},Array.Empty<int>(),Array.Empty<int>(),out _),"bounds apply independently per part");
        Check(memory.Delete("Turret roof",scope.Design,scope.Part),"a named set is explicitly deletable");
        memory.Clear();Check(memory.Names(scope.Design,scope.Part).Length==0,"leaving the design clears session selections");
        var positions=new[]{Vector3.Zero,Vector3.UnitX,Vector3.One,Vector3.UnitY};
        string geometry=SelectionMemory.GeometryFingerprint(positions,edges,faces);
        Check(geometry==SelectionMemory.GeometryFingerprint(positions.ToArray(),edges,faces),"restart geometry signature is independent of native pointers");
        var changedPositions=positions.ToArray();changedPositions[0]=new(.000001f,0,0);
        Check(geometry!=SelectionMemory.GeometryFingerprint(changedPositions,edges,faces),"even a small geometry change prevents untrusted restart rebinding");
        Check(geometry!=SelectionMemory.GeometryFingerprint(positions,edges,new[]{new[]{0,3,2,1}}),"restart signature includes ordered faces");
        string designKey=SelectionSetStore.DesignKey("C:/Factions/One","Tank A");
        Check(designKey==SelectionSetStore.DesignKey("c:\\factions\\one","Tank A"),"Windows faction path case and slash differences are normalized");
        Check(designKey!=SelectionSetStore.DesignKey("C:/Factions/Two","Tank A"),"same tank name in another faction is a different library identity");
        Check(designKey!=SelectionSetStore.DesignKey("C:/Factions/One","Tank B"),"a different named design is a different library identity");
        string directory=Path.Combine(Path.GetTempPath(),"sprocket-selection-tests-"+Guid.NewGuid().ToString("N"));
        string file=Path.Combine(directory,"selection-sets.json");
        try
        {
            var store=new SelectionSetStore(file);
            var item=new SelectionSetStore.Item("../name is metadata",designKey,20,geometry,2,new[]{1,3},new[]{2},new[]{0});
            Check(store.Save(item,out _),"selection library saves atomically");
            Check(Directory.GetFiles(directory).Length==1,"set names never become filenames and no temporary file remains");
            var loaded=new SelectionSetStore(file).ForPart(designKey,20).Single();
            Check(loaded.Name==item.Name&&loaded.Geometry==geometry&&loaded.Points.SequenceEqual(item.Points),"selection records survive a new store instance");
            loaded.Points[0]=99;
            Check(store.ForPart(designKey,20)[0].Points[0]==1,"library arrays are defensive copies");
            Check(!store.Save(item with{Points=new[]{-1}},out _),"invalid disk element indices are rejected");
            Check(!store.Save(item with{Geometry="wrong"},out _),"invalid disk geometry signature is rejected");
            Check(!store.Save(item with{Name=new string('x',61)},out _),"disk name limits match session name limits");
            Check(!store.Save(item with{Points=Enumerable.Range(0,SelectionSetStore.MaximumSelectedElements+1).ToArray()},out _),"disk selection count bound is explicit");
            Check(store.Delete(item.Name,designKey,20,out _),"persistent sets can be deleted");
            Check(new SelectionSetStore(file).ForPart(designKey,20).Length==0,"deletion survives reopening the library");
            File.WriteAllText(file,"broken JSON original");
            var broken=new SelectionSetStore(file);
            Check(broken.LoadMessage.Length>0&&!broken.Save(item,out _),"a damaged library is protected against overwrite");
            Check(File.ReadAllText(file)=="broken JSON original","recovery preserves corrupt original bytes");
            File.WriteAllText(file,"{\"Version\":2,\"Items\":[]}");
            Check(!new SelectionSetStore(file).Save(item,out _),"newer library versions are preserved rather than rewritten");
        }
        finally
        {
            string absolute=Path.GetFullPath(directory),temporaryRoot=Path.GetFullPath(Path.GetTempPath());
            if(absolute.StartsWith(temporaryRoot,StringComparison.OrdinalIgnoreCase)&&Path.GetFileName(absolute).StartsWith("sprocket-selection-tests-",StringComparison.Ordinal)
                &&Directory.Exists(absolute))Directory.Delete(absolute,true);
        }

        var cycle=new CandidateCycle();var pointer=new Vector2(100,100);var ids=new[]{4,2,8};
        Check(cycle.Choose("part-a",pointer,ids,false,0)==0,"first hover uses ordinary nearest target");
        Check(cycle.Choose("part-a",pointer,ids,true,1)==1,"cycle key advances one candidate");
        Check(cycle.Choose("part-a",pointer,ids,true,1)==1,"repeated native samples in one frame never double cycle");
        Check(cycle.Choose("part-a",pointer,new[]{8,4,2},false,2)==2,"target identity survives changed ranking");
        Check(cycle.Choose("part-a",pointer,new[]{8,4,2},true,3)==0,"cycling wraps to the first candidate");
        Check(cycle.Choose("part-a",pointer+new Vector2(10,0),ids,false,4)==0,"moving away resets cycling");
        Check(cycle.Choose("part-b",pointer,ids,false,5)==0,"switching part resets cycling");
        Check(cycle.Choose("part-b",pointer,Array.Empty<int>(),true,6)==-1,"no candidates never produces an invalid positive slot");
        Check(cycle.Choose("part-b",pointer,ids,false,7)==0,"candidate list reappearing starts at nearest");
        var hits=MeshPicking.PointHits(new[]{new MeshPicking.ScreenPoint(1,new(100,100),5),new(2,new(100,100),3),new(3,new(101,100),2)},pointer,12);
        Check(hits.Select(h=>h.Id).SequenceEqual(new[]{2,1,3}),"cycling preserves normal pixel/depth ranking");
        var visible=MeshPicking.PointHits(new[]{new MeshPicking.ScreenPoint(1,pointer,5),new(2,pointer,3)},pointer,12,accept:h=>h.Id!=2);
        Check(visible.Count==1&&visible[0].Id==1,"hidden candidates cannot be cycled unless caller allows them");
        int raycasts=0;
        var bounded=MeshPicking.PointHits(Enumerable.Range(0,1000).Select(i=>new MeshPicking.ScreenPoint(i,pointer,i+1)),pointer,12,accept:_=>{raycasts++;return true;},limit:32);
        Check(bounded.Count==32&&raycasts==32,"cycling visibility work stops at the configured candidate limit");

        Check(BuildPrediction.ClosestEdge(new(.2f,2,0),Vector3.Zero,Vector3.UnitX,out var edgePoint),"edge closest query succeeds");
        Near(edgePoint,new(.2f,0,0),"edge target may lie anywhere, not only midpoint");
        BuildPrediction.ClosestEdge(new(2,2,0),Vector3.Zero,Vector3.UnitX,out edgePoint);Near(edgePoint,Vector3.UnitX,"edge projection clamps at endpoints");
        BuildPrediction.ClosestEdge(Vector3.One,Vector3.Zero,Vector3.Zero,out edgePoint);Near(edgePoint,Vector3.Zero,"zero-length edge safely becomes a point");
        Check(!BuildPrediction.ClosestEdge(new(float.NaN),Vector3.Zero,Vector3.One,out _),"invalid edge query fails safely");
        Check(BuildPrediction.ClosestFace(new(.2f,.3f,5),Vector3.Zero,Vector3.UnitX,Vector3.UnitY,out var facePoint),"face closest query succeeds");
        Near(facePoint,new(.2f,.3f,0),"surface targets project into triangle interior");
        BuildPrediction.ClosestFace(new(2,2,0),Vector3.Zero,Vector3.UnitX,Vector3.UnitY,out facePoint);Near(facePoint,new(.5f,.5f,0),"surface targets clamp to triangle boundary");
        BuildPrediction.ClosestFace(Vector3.One,Vector3.Zero,Vector3.UnitX,2*Vector3.UnitX,out facePoint);Near(facePoint,Vector3.UnitX,"degenerate face falls back to edges");
        Check(!BuildPrediction.ClosestFace(Vector3.Zero,Vector3.Zero,new(float.PositiveInfinity),Vector3.One,out _),"invalid surface data cannot create invalid suggestions");
        var random=new Random(1801);
        for(int i=0;i<500;i++)
        {
            float u=(float)random.NextDouble()*.45f,v=(float)random.NextDouble()*.45f;
            var matrix=Matrix4x4.CreateFromYawPitchRoll(i*.03f,i*.04f,i*.013f)*Matrix4x4.CreateTranslation(10,-4,8);
            var a=Vector3.Transform(Vector3.Zero,matrix);var b=Vector3.Transform(Vector3.UnitX,matrix);var c=Vector3.Transform(Vector3.UnitY,matrix);
            var expected=Vector3.Transform(new Vector3(u,v,0),matrix);
            var p=Vector3.Transform(new Vector3(u,v,(float)random.NextDouble()*4-2),matrix);
            Check(BuildPrediction.ClosestFace(p,a,b,c,out facePoint),"rotated face query succeeds");
            Near(facePoint,expected,"closest surface result respects rotated translated part coordinates",2e-5f);
        }
        BuildPrediction.Projection Project(Vector3 p)=>new(new(p.X*100,p.Y*100),p.Z+10);
        var targets=new[]{new BuildPrediction.Target(1,new(.99f,0,0)),new(2,new(1.01f,0,0)),new(3,new(1,.02f,0))};
        var query=new BuildPrediction.Query(Vector3.Zero,new(.98f,0,0),MaxCorrection:.1f);
        var first=BuildPrediction.Resolve(targets,query,Project)!.Value;
        Check(first.TargetId==1,"prediction starts with nearest eligible target");
        var next=BuildPrediction.Resolve(targets,query,Project,first,cycleNext:true)!.Value;
        Check(next.TargetId!=first.TargetId,"prediction cycle moves to a different candidate");
        var pinned=BuildPrediction.Resolve(targets,query,Project,next,preferredTargetId:next.TargetId)!.Value;
        Check(pinned.TargetId==next.TargetId,"deliberately selected prediction stays selected");
        var fallback=BuildPrediction.Resolve(targets,query,Project,next,accept:h=>h.TargetId!=next.TargetId,preferredTargetId:next.TargetId)!.Value;
        Check(fallback.TargetId!=next.TargetId,"an occluded or unsafe pinned target immediately falls back");
        var idsSeen=new HashSet<int>();var at=first;
        for(int i=0;i<6;i++){at=BuildPrediction.Resolve(targets,query,Project,at,preferredTargetId:at.TargetId,cycleNext:true)!.Value;idsSeen.Add(at.TargetId);}
        Check(idsSeen.SetEquals(new[]{1,2,3}),"prediction cycle visits every eligible target and wraps");
        // Other-part coordinates are captured into the active part's mesh frame;
        // translating, rotating, scaling or mirroring the source must not be guessed.
        var activeFrame=Matrix4x4.CreateRotationY(.7f)*Matrix4x4.CreateTranslation(5,3,-8);
        Matrix4x4.Invert(activeFrame,out var worldToActive);
        foreach(float sign in new[]{-1f,1f})
        {
            var otherFrame=Matrix4x4.CreateScale(sign*1.4f,.7f,1.2f)*Matrix4x4.CreateRotationX(.4f)*Matrix4x4.CreateTranslation(5,3,-8);
            var localTarget=new Vector3(.4f,.3f,-.2f);
            var worldTarget=Vector3.Transform(localTarget,otherFrame);
            var activeTarget=Vector3.Transform(worldTarget,worldToActive);
            Near(Vector3.Transform(activeTarget,activeFrame),worldTarget,"cross-part target uses complete source and active transforms");
            var localAnchor=activeTarget-new Vector3(.02f,0,0);
            var cross=BuildPrediction.Resolve(new[]{new BuildPrediction.Target(99,activeTarget)},new(Vector3.Zero,localAnchor,MaxCorrection:.1f),Project)!.Value;
            Near(Vector3.Transform(cross.AnchorPosition,activeFrame),worldTarget,"accepted cross-part snap maps back to exact source world point");
        }
        Console.WriteLine($"MESH_ASSIST_TESTS_OK {checks}");
    }
}
