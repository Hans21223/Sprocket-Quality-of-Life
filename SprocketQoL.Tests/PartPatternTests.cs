using System.Numerics;
using System.Text.Json.Nodes;
using SprocketQoL;

static class PartPatternTests
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool value,string reason){checks++;if(!value)throw new Exception("Part patterns: "+reason);}
        void Near(Vector3 actual,Vector3 expected,string reason)=>Check(Vector3.Distance(actual,expected)<.0001f,reason);
        void Throws(Action action,string reason){bool threw=false;try{action();}catch{threw=true;}Check(threw,reason);}
        var linear=new PartPatternMath.Options(PartPatternMath.Pattern.Linear,3,new Vector3(.5f,0,0),Vector3.UnitY,360,Vector3.Zero,1);
        var pivot=new Vector3(2,1,3);var line=PartPatternMath.Transforms(linear,pivot);
        Check(line.Length==3,"requested added-copy count");
        for(int i=0;i<line.Length;i++)Near(Vector3.Transform(pivot,line[i]),pivot+new Vector3((i+1)*.5f,0,0),"linear world spacing");
        var radial=linear with{Kind=PartPatternMath.Pattern.Radial,Copies=4,Centre=pivot,Radius=2};
        var ring=PartPatternMath.Transforms(radial,pivot);var points=ring.Select(t=>Vector3.Transform(pivot,t)).ToArray();
        Near(points[0],pivot+new Vector3(2,0,0),"ring begins on projected radial direction");
        for(int i=0;i<4;i++)
        {
            Check(Math.Abs(Vector3.Distance(points[i],pivot)-2)<.0001,"ring radius constant");
            Near(points[i]+points[(i+2)%4],2*pivot,"opposite ring points symmetric");
            for(int j=i+1;j<4;j++)Check(Vector3.Distance(points[i],points[j])>.1f,"full ring has no repeated endpoint");
        }
        var arc=PartPatternMath.Transforms(radial with{SweepDegrees=180,Copies=3},pivot);
        Near(Vector3.Transform(pivot,arc[0]),pivot+new Vector3(2,0,0),"arc includes beginning");
        Near(Vector3.Transform(pivot,arc[^1]),pivot-new Vector3(2,0,0),"arc includes endpoint");
        var fixedOrientation=PartPatternMath.Transforms(radial with{RotateCopies=false},pivot);
        Near(Vector3.TransformNormal(Vector3.UnitZ,fixedOrientation[1]),Vector3.UnitZ,"rotation can be disabled");
        Near(Vector3.TransformNormal(Vector3.UnitZ,ring[1]),Vector3.UnitX,"rotation follows radial angle");
        Throws(()=>PartPatternMath.Transforms(linear with{Copies=101},pivot),"copy count limit");
        // Review WF-03: copies stacked by a sweep of more than a turn (the review's exact case first), or one ending on the original.
        var around=new PartPatternMath.Options(PartPatternMath.Pattern.Radial,4,Vector3.Zero,Vector3.UnitY,720,Vector3.Zero,0);
        foreach(float sweep in new[]{720f,-720f,1080f,359.99f,-359.99f})
            Throws(()=>PartPatternMath.Transforms(around with{SweepDegrees=sweep},Vector3.UnitX),$"a {sweep} degree sweep that stacks copies is refused");
        foreach(float sweep in new[]{360f,-360f,180f,540f})
        {
            var spots=PartPatternMath.Transforms(around with{SweepDegrees=sweep,Copies=sweep==540?2:4},Vector3.UnitX).Select(t=>Vector3.Transform(Vector3.UnitX,t)).Prepend(Vector3.UnitX).ToArray();
            Check(spots.Select((p,i)=>spots.Skip(i+1).All(q=>Vector3.Distance(p,q)>.01f)).All(x=>x),$"a {sweep} degree sweep gives copies all in different places, none on the original");
        }
        Throws(()=>PartPatternMath.Transforms(linear with{Spacing=Vector3.Zero},pivot),"overlapping zero-spacing line rejected");
        Throws(()=>PartPatternMath.Transforms(radial with{Axis=Vector3.Zero},pivot),"invalid axis rejected");
        Throws(()=>PartPatternMath.Transforms(radial with{Radius=float.NaN},pivot),"non-finite pattern rejected");
        Throws(()=>PartPatternMath.Transforms(radial with{SweepDegrees=0},pivot),"zero sweep rejected");
        var elevated=PartPatternMath.Transforms(radial with{Centre=Vector3.Zero,Radius=0,Copies=3},pivot).Select(t=>Vector3.Transform(pivot,t)).ToArray();
        foreach(var p in elevated) { Check(Math.Abs(p.Y-pivot.Y)<.00001,"radial patterns preserve axial height");Check(Vector3.Distance(p,pivot)>.1,"copies never overlap the original on its own ring"); }
        Check(elevated.All(p=>Math.Abs(new Vector2(p.X,p.Z).Length()-new Vector2(pivot.X,pivot.Z).Length())<.0001),"radius zero preserves radial distance");

        JsonObject Part(int id,int parent,Vector3 position,string label)=>new()
        {
            ["guid"]=Conversion.AddonGuid,["vuid"]=id,["pvuid"]=parent,["flags"]=0,["label"]=label,
            ["transform"]=new JsonObject{["pos"]=new JsonArray(position.X,position.Y,position.Z),["rot"]=new JsonArray(0,0,0,0),["scale"]=new JsonArray(1,1,1),["mirrorVuid"]=-1}
        };
        var design=new JsonObject
        {
            ["header"]=new JsonObject{["name"]="Pattern fixture"},["blueprints"]=new JsonArray(),["meshes"]=new JsonArray(),
            ["objects"]=new JsonArray(Part(0,-1,Vector3.Zero,"hull"),Part(10,0,new Vector3(2,1,3),"root"),Part(20,10,new Vector3(0,0,.2f),"child"))
        };
        design["objects"]![1]!["customPaint"]="olive";
        design["blueprints"]!.AsArray().Add(JsonNode.Parse("""{"id":50,"type":"paintJob","blueprint":{"name":"Green","description":"Quality of Life parts: 10 999","r":0.25,"g":0.4,"b":0.15}}"""));
        design["blueprints"]!.AsArray().Add(JsonNode.Parse("""{"id":51,"type":"paintJobRegister","blueprint":{"paintJobIDs":[-1,-1,-1,-1,-1,50]}}"""));
        var painted=PartPatternMath.Apply(design.ToJsonString(),new[]{10},linear);
        var paintBlocks=Conversion.Parse(painted.Json)["blueprints"]!.AsArray();
        var paint=paintBlocks.Single(b=>b?["type"]?.GetValue<string>()=="paintJob")!["blueprint"]!;
        var paintedIds=paint["description"]!.GetValue<string>().Split(' ',StringSplitOptions.RemoveEmptyEntries).Skip(4).Select(int.Parse).ToHashSet();
        var copiedIds=Conversion.Objects(Conversion.Parse(painted.Json)).Values.Where(o=>o["label"]?.GetValue<string>()=="root").Select(o=>Conversion.Id(o,"vuid")).ToArray();
        Check(copiedIds.All(paintedIds.Contains),"each patterned root retains its custom paint ownership");
        Check(paintBlocks.Count(b=>b?["type"]?.GetValue<string>()=="paintJob")==1,"identical copied paint reuses the existing custom slot");
        var foreign=JsonNode.Parse(design.ToJsonString())!.AsObject(); foreign["blueprints"]!.AsArray().RemoveAt(0); foreign["blueprints"]![0]!["blueprint"]!["paintJobIDs"]=new JsonArray(-1,-1,-1,-1,-1);
        var clip=BlueprintClipboard.Copy(design.ToJsonString(),new[]{10}).ClipboardJson;
        var pasted=Conversion.Parse(BlueprintClipboard.PasteInto(foreign.ToJsonString(),clip,0).Json);
        var pastedPaint=pasted["blueprints"]!.AsArray().Single(b=>b?["type"]?.GetValue<string>()=="paintJob")!["blueprint"]!;
        Check(!pastedPaint["description"]!.GetValue<string>().Split(' ').Contains("999"),"paint copied to another vehicle excludes unselected owners");
        Check(pastedPaint["r"]!.GetValue<float>()==.25f,"paint channels survive clipboard transport");
        var cutPaint=Conversion.Parse(BlueprintClipboard.RemoveFrom(design.ToJsonString(),new[]{10}))["blueprints"]!.AsArray().Single(b=>b?["type"]?.GetValue<string>()=="paintJob")!["blueprint"]!;
        Check(cutPaint["description"]!.GetValue<string>()=="Quality of Life parts: 999","cut removes dead paint owners while keeping other ownership");
        string source=design.ToJsonString();
        Near(PartPatternMath.SelectionPivot(source,new[]{10,20}),pivot,"selected child is excluded from root pivot");
        var made=PartPatternMath.Apply(source,new[]{10},linear);
        // Review PERF-02: one tree for all copies gives the very file pasting them one by one gave.
        {
            var (copied,_)=BlueprintClipboard.Copy(source,new[]{10});
            string oneByOne=source;
            foreach(var t in PartPatternMath.Transforms(linear,PartPatternMath.SelectionPivot(source,new[]{10})))
            {
                var c=JsonNode.Parse(copied)!.AsObject();var roots=c["rootTransforms"]!.AsObject();
                foreach(var pair in roots.ToArray())
                {
                    var a=pair.Value!.AsArray().Select(v=>v!.GetValue<float>()).ToArray();
                    var w=new Matrix4x4(a[0],a[1],a[2],a[3],a[4],a[5],a[6],a[7],a[8],a[9],a[10],a[11],a[12],a[13],a[14],a[15])*t;
                    roots[pair.Key]=new JsonArray(new[]{w.M11,w.M12,w.M13,w.M14,w.M21,w.M22,w.M23,w.M24,w.M31,w.M32,w.M33,w.M34,w.M41,w.M42,w.M43,w.M44}.Select(v=>(JsonNode?)JsonValue.Create(v)).ToArray());
                }
                oneByOne=BlueprintClipboard.PasteInto(oneByOne,c.ToJsonString(),Conversion.Id(Conversion.Objects(Conversion.Parse(source))[10],"pvuid")).Json;
            }
            Check(made.Json==oneByOne,"all copies pasted into one tree give the same design as pasting them one by one");
        }
        var objects=Conversion.Objects(Conversion.Parse(made.Json));var world=Conversion.WorldMatrices(objects);
        Check(made.AddedParts==6&&objects.Count==9,"copies include attached children and preserve original tree");
        Check(objects.Keys.Count==objects.Keys.Distinct().Count(),"all copied IDs unique");
        Check(Conversion.Parse(source).ToJsonString()==design.ToJsonString(),"planning does not mutate input JSON");
        var copiedRoots=objects.Values.Where(o=>o["label"]?.GetValue<string>()=="root"&&Conversion.Id(o,"vuid")!=10).OrderBy(o=>world[Conversion.Id(o,"vuid")].Translation.X).ToArray();
        for(int i=0;i<3;i++)
        {
            int id=Conversion.Id(copiedRoots[i],"vuid");Near(world[id].Translation,pivot+new Vector3((i+1)*.5f,0,0),"planned root keeps world position plus spacing");
            Check(copiedRoots[i]["customPaint"]?.GetValue<string>()=="olive","copy retains per-part paint settings");
            var child=objects.Values.Single(o=>Conversion.Id(o,"pvuid")==id);
            Near(world[Conversion.Id(child,"vuid")].Translation-world[id].Translation,new Vector3(0,0,.2f),"attached child offset preserved");
        }
        var round=PartPatternMath.Apply(source,new[]{10},radial);var roundObjects=Conversion.Objects(Conversion.Parse(round.Json));var roundWorld=Conversion.WorldMatrices(roundObjects);
        Check(round.AddedParts==8&&roundObjects.Count==11,"radial hierarchy copy count");
        foreach(var o in roundObjects.Values.Where(o=>o["label"]?.GetValue<string>()=="root"&&Conversion.Id(o,"vuid")!=10))
            Check(Math.Abs(Vector3.Distance(roundWorld[Conversion.Id(o,"vuid")].Translation,pivot)-2)<.0001,"radial copies lie on requested circle");
        Throws(()=>PartPatternMath.Apply(source,new[]{0},linear),"main hull patterns rejected");
        Throws(()=>PartPatternMath.Apply(source,new[]{999},linear),"stale selections rejected");
        Throws(()=>PartPatternMath.Apply(source,new[]{10},linear with{Copies=5000}),"excessive copy counts rejected before editing");
        Check(!made.Json.Contains("NaN")&&!made.Json.Contains("Infinity"),"finite transforms serialized");
        var shapeFixture=Conversion.Parse(source);
        shapeFixture["objects"]![1]!["structureBlueprintVuid"]=100;
        shapeFixture["blueprints"]!.AsArray().Add(JsonNode.Parse("""{"id":100,"type":"structure","blueprint":{"bodyMeshVuid":200}}"""));
        var geometry=AddonEdits.MeshData(new(){Vector3.Zero,Vector3.UnitX,Vector3.UnitY},new(){new[]{0,1,2}},10,30);
        shapeFixture["meshes"]!.AsArray().Add(new JsonObject{["vuid"]=200,["meshData"]=geometry});
        var shapeClip=BlueprintClipboard.Copy(shapeFixture.ToJsonString(),new[]{10}).ClipboardJson;
        var thumbnail=WorkshopGeometry.Read(shapeClip);
        Check(thumbnail.Meshes.Length==1&&thumbnail.Meshes[0].Faces.Length==1,"thumbnail reads actual saved plate structure geometry");
        Near(GeometryPreviewMath.ImportPosition(thumbnail.Meshes[0].Vertices[0]),pivot,"thumbnail preserves the copied root's world transform");
        Console.WriteLine($"PART_PATTERN_TESTS_OK {checks}");
    }
}
