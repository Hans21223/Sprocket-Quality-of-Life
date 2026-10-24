using System.Numerics;
using SprocketQoL;

static class PrecisionGeometryTests
{
    internal static void Run()
    {
        int checks=0;
        void Check(bool yes,string why) { checks++;if(!yes)throw new Exception(why); }
        var quad=new[]{Vector3.Zero,Vector3.UnitX,new Vector3(1,1,0),Vector3.UnitY};
        var face=new[]{new[]{0,1,2,3}};
        foreach(int cuts in new[]{1,2,4,8,16}) foreach(float offset in new[]{.01f,.25f,.5f,.75f,.99f})
        {
            var plan=PrecisionGeometry.LoopCuts(quad,face,new[]{(0,1)},cuts,offset);
            Check(plan.Why==null,$"loop cut {cuts}/{offset}: {plan.Why}");
            Check(plan.Add.Count==cuts+1&&plan.Points.Count==2*cuts,"each cut adds exactly one strip boundary with no unrelated points");
            Check(MeshPlans.Check(quad,face,plan,true)==null,"offset loop topology passes normal geometric checks");
            var all=quad.Concat(plan.Points.Select(p=>p.P)).ToArray();
            Check(Math.Abs(plan.Add.Sum(f=>HoleRing.Normal(f.Corners.Select(v=>all[v]).ToArray()).Length()/2)-1)<1e-5f,"strips cover exactly the original plate");
            Check(plan.Points.All(p=>Math.Abs(p.Blend.Sum(b=>b.W)-1)<1e-6f&&p.Blend.All(b=>b.W>0)),"new corner interpolation is normalized and inside its source edge");
        }
        var strip=new[]{Vector3.Zero,Vector3.UnitX,new Vector3(2,0,0),Vector3.UnitY,new Vector3(1,1,0),new Vector3(2,1,0)};
        var stripFaces=new[]{new[]{0,1,4,3},new[]{1,2,5,4}};
        foreach(var start in new[]{(0,3),(3,0),(2,5)})
        {
            var plan=PrecisionGeometry.LoopCuts(strip,stripFaces,new[]{start},3,.25f);
            Check(plan.Why==null&&plan.Add.Count==8,"multi-cut follows two adjoining quads");
            Check(MeshPlans.Check(strip,stripFaces,plan,true)==null,"shared edges stay connected when the loop traverses a strip");
        }
        Check(PrecisionGeometry.LoopCuts(quad,face,new[]{(0,1)},0,.5f).Why!=null,"reject zero cuts");
        Check(PrecisionGeometry.LoopCuts(quad,face,new[]{(0,1)},1,0).Why!=null,"reject collapsed boundary offset");
        var slide=PrecisionGeometry.Slide(quad,face,new[]{0,1},25,false,false);
        Check(slide.Count==2&&Vector3.Distance(slide[0],new Vector3(0,.25f,0))<1e-6f&&Vector3.Distance(slide[1],new Vector3(1,.25f,0))<1e-6f,"an entire selected edge slides along both connected rails");
        Check(MeshPlans.Folds(quad,face,slide)==null,"a safe slide keeps plate winding");
        var slideMm=PrecisionGeometry.Slide(quad,face,new[]{0,1},250,true,false);
        Check(slideMm.All(p=>Vector3.Distance(p.Value,slide[p.Key])<1e-6f),"millimetre and percent slides agree for the one-metre rails");
        Check(PrecisionGeometry.Slide(quad,face,new[]{0,1},100,false,false).Count==0,"reject collapsing slides");
        Check(PrecisionGeometry.Slide(quad,face,new[]{0,1},float.NaN,false,false).Count==0,"reject nonfinite slides");
        var length=PrecisionGeometry.SetDistance(quad,0,1,.3f);
        Check(length.Count==1&&length[1]==new Vector3(.3f,0,0),"exact length leaves anchor fixed and moves the other endpoint along its existing line");
        Check(PrecisionGeometry.SetDistance(quad,0,0,1).Count==0,"reject zero-length source direction");
        var shells=quad.Concat(quad.Select(p=>p+new Vector3(0,0,.01f))).ToArray();
        var shellFaces=new[]{face[0],new[]{4,5,6,7}};
        var free=PrecisionGeometry.Influence(shells,shellFaces,new[]{0},2,false,false,PrecisionGeometry.FalloffShape.Smooth);
        var connected=PrecisionGeometry.Influence(shells,shellFaces,new[]{0},2,true,false,PrecisionGeometry.FalloffShape.Smooth);
        Check(free.ContainsKey(4)&&!connected.ContainsKey(4)&&connected.ContainsKey(1),"connected-only influence excludes a close disconnected shell");
        foreach(var shape in Enum.GetValues<PrecisionGeometry.FalloffShape>())
        {
            var weights=PrecisionGeometry.Influence(shells,shellFaces,new[]{0,4},2,true,false,shape);
            Check(weights[1].Moved.All(v=>v<4)&&weights[5].Moved.All(v=>v>=4),"separately selected shells never borrow influence from each other");
            Check(weights.Values.All(w=>w.Weights.All(x=>float.IsFinite(x)&&x>0)&&w.Weights.Sum()<=1.00001f),"falloff weights stay bounded and finite");
        }
        var longRoute=new[]{Vector3.Zero,new Vector3(1,0,0),new Vector3(0,.01f,0)};
        var routeFaces=new[]{new[]{0,1},new[]{1,2}};
        Check(PrecisionGeometry.Influence(longRoute,routeFaces,new[]{0},.5f,true,false,PrecisionGeometry.FalloffShape.Linear).ContainsKey(2),"straight-line falloff reaches the folded-back rail");
        Check(!PrecisionGeometry.Influence(longRoute,routeFaces,new[]{0},.5f,true,true,PrecisionGeometry.FalloffShape.Linear).ContainsKey(2),"topology-distance influence follows the long connected route");
        // Review PERF-01: the grid of seeds gives exactly what checking every seed gave, on a dense random grid of plates.
        static Dictionary<int,(int[] Moved,float[] Weights)> EverySeed(Vector3[] p,int[][] faces,int[] selected,float radius,PrecisionGeometry.FalloffShape shape)
        {
            var result=new Dictionary<int,(int[],float[])>();
            for(int v=0;v<p.Length;v++)
            {
                if(selected.Contains(v)) continue;
                var near=selected.Select(m=>(M:m,D:Vector3.Distance(p[v],p[m]))).Where(x=>x.D<radius).ToArray();
                if(near.Length==0) continue;
                float x=Math.Clamp(near.Min(n=>n.D)/radius,0,1);
                float fall=shape switch { PrecisionGeometry.FalloffShape.Linear=>1-x,PrecisionGeometry.FalloffShape.Sharp=>(1-x)*(1-x),PrecisionGeometry.FalloffShape.Constant=>1,_=>1-x*x*(3-2*x) };
                var w=near.Select(n=>1/(n.D*n.D+1e-8f)).ToArray();float sum=w.Sum();
                result[v]=(near.Select(n=>n.M).ToArray(),w.Select(q=>fall*q/sum).ToArray());
            }
            return result;
        }
        var random=new Random(7);
        var dense=Enumerable.Range(0,40*40).Select(i=>new Vector3(i%40*.05f+(float)random.NextDouble()*.01f,(float)random.NextDouble()*.02f,i/40*.05f)).ToArray();
        var denseFaces=Enumerable.Range(0,39*39).Select(i=>{int a=i%39+i/39*40;return new[]{a,a+1,a+41,a+40};}).ToArray();
        var picked=Enumerable.Range(0,dense.Length).Where(_=>random.Next(5)==0).ToArray();
        foreach(float r in new[]{.03f,.12f,.5f})
        {
            var fast=PrecisionGeometry.Influence(dense,denseFaces,picked,r,false,false,PrecisionGeometry.FalloffShape.Smooth);
            var slow=EverySeed(dense,denseFaces,picked,r,PrecisionGeometry.FalloffShape.Smooth);
            Check(fast.Count==slow.Count&&slow.All(kv=>fast.TryGetValue(kv.Key,out var f)&&f.Moved.SequenceEqual(kv.Value.Moved)&&f.Weights.SequenceEqual(kv.Value.Weights)),
                $"influence by grid matches every-seed influence exactly at radius {r}");
        }
        var h=PrecisionGeometry.Inspect(quad,face);
        Check(h.OpenEdges==4&&h.DuplicatePoints==0&&h.NonManifoldEdges==0&&h.DegenerateFaces==0,"a plain open plate is reported as an intentional border without invalid geometry");
        var dup=PrecisionGeometry.Inspect(quad.Concat(new[]{new Vector3(-.0000001f,0,0)}).ToArray(),face);
        Check(dup.DuplicatePoints==1&&dup.ProblemVertices.Contains(0)&&dup.ProblemVertices.Contains(4),"duplicate detection crosses quantization-cell boundaries");
        var broken=PrecisionGeometry.Inspect(quad,new[]{new[]{0,1,1},new[]{0,1,2},new[]{1,0,3},new[]{0,1,3}});
        Check(broken.InvalidFaces==1&&broken.NonManifoldEdges==1,"invalid corner loops and edges shared by three faces are reported");
        Check(quad.SequenceEqual(new[]{Vector3.Zero,Vector3.UnitX,new Vector3(1,1,0),Vector3.UnitY}),"all precision operations preserve the supplied source positions");
        var seam=new[]{Vector3.Zero,Vector3.UnitX,new Vector3(1,1,0),Vector3.UnitY,Vector3.UnitX,new Vector3(2,0,0),new Vector3(2,1,0),new Vector3(1,1,0)};
        var seamFaces=new[]{new[]{0,1,2,3},new[]{4,5,6,7}};
        var weld=PrecisionGeometry.WeldDuplicates(seam,seamFaces,new[]{1,2,4,7});
        Check(weld.Why==null&&weld.Remove.Count==1&&weld.Add[0].Corners.SequenceEqual(new[]{1,5,6,2}),"weld joins only the chosen coincident seam points");
        Check(PrecisionGeometry.WeldDuplicates(seam,seamFaces,new[]{1,2}).Why!=null,"unselected duplicates stay untouched");
        Check(PrecisionGeometry.WeldDuplicates(new[]{Vector3.Zero,Vector3.Zero,Vector3.UnitX},new[]{new[]{0,1,2}},new[]{0,1}).Why!=null,"weld refuses to collapse a face");
        var lifted=quad.Select(p=>p+new Vector3(0,0,.075f)).ToArray();
        Check(Math.Abs(PrecisionGeometry.Clearance(quad,lifted)-.075f)<1e-6,"clearance measures parallel face separation");
        var shifted=quad.Select(p=>p+new Vector3(2,0,0)).ToArray();
        Check(Math.Abs(PrecisionGeometry.Clearance(quad,shifted)-1)<1e-6,"coplanar disjoint faces have nonzero edge clearance");
        var crossing=new[]{new Vector3(.5f,.2f,-1),new Vector3(.5f,.8f,-1),new Vector3(.5f,.8f,1),new Vector3(.5f,.2f,1)};
        Check(PrecisionGeometry.Clearance(quad,crossing)<1e-6,"clearance detects an edge piercing a face");
        var aligned=PrecisionGeometry.Align(quad,new[]{0,1,2,3},Vector3.UnitY,Vector3.UnitZ);
        Check(aligned.Count==4&&Vector3.Distance(aligned.Values.Aggregate(Vector3.Zero,(a,b)=>a+b)/4,new Vector3(.5f,.5f,0))<1e-6,"parallel alignment preserves selection centre");
        Check(Math.Abs(Vector3.Distance(aligned[0],aligned[2])-MathF.Sqrt(2))<1e-6,"parallel alignment rotates rigidly without changing distances");
        Check(PrecisionGeometry.Align(quad,new[]{0,1},Vector3.UnitX,-Vector3.UnitX).Count==0,"antiparallel lines already count as aligned");
        Check(PrecisionGeometry.LoopCuts(quad,face,new[]{(0,1),(1,2)},1,.5f).Why!=null,"crossing loop-cut directions are rejected without loose preview points");
        // Review GEO-03: Align turning one side of a thin plate round makes a bow tie whose two halves still sum to the old facing.
        var thin=new[]{new Vector3(0,0,0),new Vector3(2,0,0),new Vector3(2,.2f,0),new Vector3(0,.2f,0)};
        var tie=PrecisionGeometry.Align(thin,new HashSet<int>{0,1},thin[1]-thin[0],new Vector3(-1,MathF.Sqrt(3),0));
        Check(MeshPlans.Folds(thin,face,tie)?.Contains("cross")==true,"a bow tie made by Align is refused");
        Check(MeshPlans.Folds(thin,face,tie.ToDictionary(kv=>kv.Key,kv=>new Vector3(kv.Value.X,kv.Value.Y,kv.Value.Z+.05f*kv.Key)))?.Contains("cross")==true,
            "a warped bow tie is refused too");
        var mirroredThin=thin.Select(p=>new Vector3(-p.X,p.Y,p.Z)).ToArray();
        var mirroredTie=PrecisionGeometry.Align(mirroredThin,new HashSet<int>{0,1},mirroredThin[1]-mirroredThin[0],new Vector3(1,MathF.Sqrt(3),0));
        Check(MeshPlans.Folds(mirroredThin,new[]{new[]{3,2,1,0}},mirroredTie)?.Contains("cross")==true,"the mirrored bow tie, wound the other way, is refused");
        Check(MeshPlans.Folds(thin,face,new Dictionary<int,Vector3>{[2]=new(1,.05f,0)})==null,"a quad with a hollow corner (concave) is still allowed");
        Check(MeshPlans.Folds(thin,face,new Dictionary<int,Vector3>{[2]=new(2,.3f,.1f)})==null,"a plain warped quad is still allowed");
        Check(MeshPlans.Folds(thin,face,new Dictionary<int,Vector3>{[0]=new(.1f,0,0),[1]=new(1.9f,.01f,0)})==null,"an ordinary small turn is still allowed");
        // Uneven halves, the bigger one facing the old way: neither "turned over" nor "squashed" sees it.
        var uneven=new[]{new Vector3(0,0,0),new Vector3(3,0,0),new Vector3(2,1.5f,0),new Vector3(0,1,0)};
        var crossedPlan=new MeshPlans.Rebuild(new(){0},new(){new(new[]{3,1,2,0},0)},new(),null);
        Check(MeshPlans.Check(uneven,face,crossedPlan)?.Contains("cross")==true,"a new face listed as a bow tie is refused before anything changes");
        Console.WriteLine($"PRECISION_GEOMETRY_TESTS_OK: {checks} checks");
    }
}
