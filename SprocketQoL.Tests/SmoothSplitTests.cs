using System.Numerics;
using SprocketQoL;

static class SmoothSplitTests
{
    static int checks;
    static void Check(bool ok,string why) { checks++; if(!ok) throw new Exception(why); }
    static IEnumerable<(int A,int B)> Sides(int[] f) => f.Select((v,k)=>(v,f[(k+1)%f.Length]));
    static void Valid(List<Vector3> p,List<int[]> faces,MeshPlans.Rebuild plan,bool closed)
    {
        Check(plan.Why==null,"plan: "+plan.Why);
        Check(MeshPlans.Check(p,faces,plan)==null,"mesh: "+MeshPlans.Check(p,faces,plan));
        var all=p.Concat(plan.Points.Select(v=>v.P)).ToList();
        Check(all.All(v=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z)),"finite points");
        var after=faces.Where((f,i)=>!plan.Remove.Contains(i)).Concat(plan.Add.Select(f=>f.Corners)).ToList();
        var uses=after.SelectMany(Sides).GroupBy(e=>FaceMerge.Key(e.A,e.B));
        foreach(var e in uses)
        {
            Check(e.Count()<3 && (!closed||e.Count()==2),"manifold and closed boundary");
            if(e.Count()==2) Check(e.First().A==e.Last().B,"opposite winding along shared edge");
        }
        Check(plan.Add.All(f=>f.Corners.Length is 3 or 4),"game supports only triangles and quads");
        Check(Enumerable.Range(p.Count,plan.Points.Count).All(v=>after.Any(f=>f.Contains(v))),"no unused added points");
    }
    internal static void Run()
    {
        checks=0;
        var p=Enumerable.Range(0,8).Select(i=>new Vector3(i&1,(i>>1)&1,(i>>2)&1)).ToList();
        var faces=new List<int[]> {new[]{0,1,5,4},new[]{2,3,7,6},new[]{0,2,6,4},new[]{1,3,7,5},new[]{0,1,3,2},new[]{4,5,7,6}};
        faces=faces.Select(f=>Vector3.Dot(HoleRing.Normal(f.Select(v=>p[v]).ToList()),f.Aggregate(Vector3.Zero,(s,v)=>s+p[v])/4-new Vector3(.5f))<0?f.Reverse().ToArray():f).ToList();
        var edges=faces.SelectMany(Sides).Select(e=>FaceMerge.Key(e.A,e.B)).Distinct().ToArray();
        foreach(var selection in new[]{new[]{(2,3)},new[]{(2,3),(3,7)},new[]{(3,7),(5,7),(6,7)},new[]{(2,3),(3,7),(7,6),(6,2)},edges})
            foreach(int segments in new[]{2,4,8,16})
            {
                var round=MeshPlans.Bevel(p,faces,selection,.1f,segments);
                if (MeshPlans.Check(p,faces,round) is string error)
                {
                    var debugPoints=p.Concat(round.Points.Select(v=>v.P)).ToArray();
                    throw new Exception($"Round {string.Join(';',selection)} segments {segments}: {error}; small faces: "+string.Join(" / ",round.Add.Where(f=>HoleRing.Normal(f.Corners.Select(v=>debugPoints[v]).ToList()).Length()<1e-5f).Select(f=>string.Join(',',f.Corners))));
                }
                Valid(p,faces,round,true);
                Check(round.Add.Count>MeshPlans.Bevel(p,faces,selection,.1f).Add.Count,"rounding adds multiple surfaces");
                var roundedSources=BevelEdges.Sources(p,faces,round,selection.Select(e=>FaceMerge.Key(e.Item1,e.Item2)).ToHashSet());
                Check(roundedSources.All(e=>!selection.Contains(e.Value)),"rounded strips do not inherit sharp or welded flags from removed edges");
                var before=p.ToArray();
                var split=EdgeSubdivision.Split(p,faces,selection,segments);
                Valid(p,faces,split,true);
                Check(split.Add.All(f=>f.Corners.Length==4),"box splits produce only quad strips or grids, never diagonal fans");
                Check(split.Points.Select(v=>v.P).Distinct().Count()==split.Points.Count,"shared cut edges have no duplicate points");
                Check(p.SequenceEqual(before),"planning leaves original mesh untouched");
                var sources=BevelEdges.Sources(p,faces,split);
                var all=p.Concat(split.Points.Select(v=>v.P)).ToList();
                foreach(var (a,b) in split.Add.SelectMany(f=>Sides(f.Corners)))
                {
                    var d=Vector3.Abs(all[a]-all[b]);
                    Check(new[]{d.X,d.Y,d.Z}.Count(v=>v>1e-6f)==1,"every split on a box is a straight axis-aligned line");
                }
                foreach(var (a,b) in selection)
                {
                    var along=Enumerable.Range(0,all.Count).Where(v=>Vector3.Distance(all[v],p[a])+Vector3.Distance(all[v],p[b])<Vector3.Distance(p[a],p[b])+1e-5f).OrderBy(v=>Vector3.DistanceSquared(all[v],p[a])).ToArray();
                    Check(along.Length==segments+1,"requested number of subdivisions");
                    for(int i=0;i<segments;i++) Check(sources[FaceMerge.Key(along[i],along[i+1])]==FaceMerge.Key(a,b),"subedges retain original edge settings");
                }
            }
        var curve=MeshPlans.Bevel(p,faces,new[]{(2,3)},.1f,4);
        // The end arc on the cube follows a circle, not a subdivided flat chamfer.
        var arc=curve.Points.Where(v=>Math.Abs(v.P.X)<1e-6 && v.Blend[0].V==2).Select(v=>v.P).ToArray();
        Check(arc.Length==5,"four segments create five points on the curved profile");
        foreach(var v in arc) Check(Math.Abs(Vector3.Distance(v,new(0,.9f,.1f))-.1f)<1e-5,"circular quarter-round profile");
        var triP=new List<Vector3>{new(0,0,0),new(1,0,0),new(0,1,0)};
        var triF=new List<int[]>{new[]{0,1,2}};
        Valid(triP,triF,EdgeSubdivision.Split(triP,triF,new[]{(0,1),(1,2)},5),false);
        var oneEdge=EdgeSubdivision.Split(p,faces,new[]{(2,3)},4);
        Check(oneEdge.Remove.Count==4 && oneEdge.Add.Count==16 && oneEdge.Points.Count==12,"single edge makes four straight strips around the box, leaving end faces untouched");
        var bothEdges=EdgeSubdivision.Split(p,faces,new[]{(2,3),(3,2)},4);
        Check(bothEdges.Points.Count==oneEdge.Points.Count && bothEdges.Add.Count==oneEdge.Add.Count,"duplicate or reversed selections do not double the cuts");
        foreach(int n in new[]{2,4,16})
        {
            int target=0; var c=faces[target]; var scope=new HashSet<int>{target};
            var local=EdgeSubdivision.Split(p,faces,new[]{(c[0],c[1])},n,scope);
            Check(local.Why==null && MeshPlans.Check(p,faces,local)==null,"local split stays closed without propagating cuts");
            var coords=p.Concat(local.Points.Select(v=>v.P)).ToArray();
            var newTarget=local.Add.Where(f=>f.Source==target).ToList();
            Check(newTarget.Count==n && newTarget.All(f=>f.Corners.Length==4),"only selected face becomes strips");
            Check(local.Add.All(f => f.Corners.Length is 3 or 4), "local splits and neighbours remain loadable triangles or quads");
            foreach (var group in local.Add.Where(f => f.Source != target).GroupBy(f => f.Source))
            {
                var normal = HoleRing.Normal(faces[group.Key].Select(v => p[v]).ToList());
                var corner = p[faces[group.Key][0]];
                Check(group.SelectMany(f => f.Corners).All(v => MathF.Abs(Vector3.Dot(coords[v] - corner, normal)) < 1e-6f),
                    "neighbour tessellation preserves its original plate plane");
                Check(MathF.Abs(group.Sum(f => HoleRing.Normal(f.Corners.Select(v => coords[v]).ToList()).Length()) - normal.Length()) < 1e-5f,
                    "neighbour tessellation preserves area and winding");
            }
            Check(local.Points.Count==2*(n-1),"only the two selected-face borders gain vertices");
            var result=faces.Where((f,i)=>!local.Remove.Contains(i)).Concat(local.Add.Select(f=>f.Corners)).ToList();
            var changedSides = local.Remove.Where(f => f != target).ToArray();
            Check(changedSides.All(f => faces[f].Intersect(c).Count() == 2), "only neighbours sharing a split border are rebuilt");
            Check(result.SelectMany(Sides).GroupBy(e=>FaceMerge.Key(e.A,e.B)).All(g=>g.Count()==2 && g.First().A==g.Last().B),"local split shares every border edge with the intact neighbour");
            var saved=newTarget.Select(f=>f.Corners.Select(v=>coords[v]).ToArray()).ToList();
            Check(result.Count(f=>SplitFaceSelection.Matches(f.Select(v=>coords[v]).ToArray(),saved))==n,"select-between finds every strip and no neighbouring face");
            Check(!SplitFaceSelection.Matches(c.Select(v=>p[v]).ToArray(),saved),"undo does not accidentally select the original face");
            Check(SplitFaceSelection.Matches(saved[0].Reverse().ToArray(),saved),"selection survives recreated or reversed face ordering");
            Check(!SplitFaceSelection.Matches(saved[0].Select(v=>v+new Vector3(.01f,0,0)).ToArray(),saved),"edited geometry invalidates stale selection");
        }
        var triangleGrid=EdgeSubdivision.Split(triP,triF,new[]{(0,1)},5);
        Check(triangleGrid.Add.Count==25,"triangle terminates with an even triangular grid instead of an apex fan");
        var mixedP=new List<Vector3>{new(0,0,0),new(1,0,0),new(1,1,0),new(0,1,0),new(2,.5f,0)};
        var mixedF=new List<int[]>{new[]{0,1,2,3},new[]{1,4,2}};
        Valid(mixedP,mixedF,EdgeSubdivision.Split(mixedP,mixedF,new[]{(0,3)},4),false);
        foreach (int sections in new[] { 2, 4, 16 })
        {
            Valid(mixedP, mixedF, EdgeSubdivision.Split(mixedP, mixedF, new[] { (0, 3) }, sections, new HashSet<int> { 0 }), false);
            var localOrientation = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), .71f);
            var placed = p.Select(v => Vector3.Transform(v, localOrientation) + new Vector3(5, -2, 7)).ToList();
            var reversed = faces.Select(f => f.Reverse().ToArray()).ToList();
            foreach (var winding in new[] { faces, reversed })
                foreach (int side in new[] { 0, 1 })
                    Valid(placed, winding, EdgeSubdivision.Split(placed, winding,
                        new[] { (winding[0][side], winding[0][side + 1]) }, sections, new HashSet<int> { 0 }), true);
        }
        var concavePoints = new List<Vector3> { new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0), new(2,0,0), new(1.3f,.3f,0) };
        var concaveFaces = new List<int[]> { new[] { 0,1,2,3 }, new[] { 1,4,5,2 } };
        foreach (int sections in new[] { 2, 4, 16 })
            Valid(concavePoints, concaveFaces, EdgeSubdivision.Split(concavePoints, concaveFaces,
                new[] { (0,3) }, sections, new HashSet<int> { 0 }), false);
        var octaP=new List<Vector3>{Vector3.UnitX,-Vector3.UnitX,Vector3.UnitY,-Vector3.UnitY,Vector3.UnitZ,-Vector3.UnitZ};
        var octaF=new List<int[]>();
        foreach(int a in new[]{0,1}) foreach(int b in new[]{2,3}) foreach(int c in new[]{4,5})
        {
            var f=new[]{a,b,c};
            if(Vector3.Dot(HoleRing.Normal(f.Select(v=>octaP[v]).ToList()),octaP[a]+octaP[b]+octaP[c])<0) Array.Reverse(f);
            octaF.Add(f);
        }
        foreach(int n in new[]{2,4,8}) Valid(octaP,octaF,MeshPlans.Bevel(octaP,octaF,new[]{(0,2)},.1f,n),true);
        var rotation=Quaternion.CreateFromYawPitchRoll(.7f,.3f,-.2f);
        var tilted=p.Select(v=>Vector3.Transform(v,rotation)+new Vector3(10,-2,5)).ToList();
        var tiltedSplit=EdgeSubdivision.Split(tilted,faces,new[]{(2,3),(3,7)},4);
        Valid(tilted,faces,tiltedSplit,true);
        var flatSplit=EdgeSubdivision.Split(p,faces,new[]{(2,3),(3,7)},4);
        Check(tiltedSplit.Points.Zip(flatSplit.Points).All(pair=>Vector3.Distance(pair.First.P,Vector3.Transform(pair.Second.P,rotation)+new Vector3(10,-2,5))<1e-5f),"cuts follow the face orientation, independent of world axes");
        Valid(tilted,faces,MeshPlans.Bevel(tilted,faces,new[]{(2,3),(3,7)},.1f,4),true);
        var reflected=p.Select(v=>new Vector3(-v.X,v.Y,v.Z)).ToList();
        Valid(reflected,faces.Select(f=>f.Reverse().ToArray()).ToList(),MeshPlans.Bevel(reflected,faces.Select(f=>f.Reverse().ToArray()).ToList(),new[]{(2,3)},.1f,4),true);
        Check(EdgeSubdivision.Split(p,faces,Array.Empty<(int,int)>(),4).Why!=null,"empty selection rejected");
        Check(EdgeSubdivision.Split(p,faces,new[]{(0,7)},4).Why!=null,"non-edge rejected");
        Check(EdgeSubdivision.Split(p,faces,new[]{(2,3)},4,new HashSet<int>{0}).Why!=null,"a scoped split cannot modify a face outside the selection");
        Check(EdgeSubdivision.Split(new[]{new Vector3(float.NaN,0,0)},new[]{new[]{0,0,0}},new[]{(0,0)},4).Why!=null,"split rejects invalid geometry atomically");
        Check(MeshPlans.Bevel(p,faces,new[]{(2,3)},float.NaN,4).Why!=null,"invalid width rejected");
        Check(MeshPlans.Bevel(p,faces,new[]{(2,3)},.1f,17).Why!=null,"unbounded segment count rejected");
        Console.WriteLine($"SMOOTH_SPLIT_TESTS_OK: {checks} checks");
    }
}
