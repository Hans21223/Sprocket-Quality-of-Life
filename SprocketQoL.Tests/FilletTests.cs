using System.Numerics;
using SprocketQoL;

static class FilletTests
{
    static int checks;
    static void Check(bool ok,string message) { checks++; if(!ok) throw new Exception(message); }
    static IEnumerable<(int A,int B)> Sides(int[] face) => face.Select((v,k)=>(v,face[(k+1)%face.Length]));
    static void Valid(IReadOnlyList<Vector3> points,IReadOnlyList<int[]> faces,MeshPlans.Rebuild plan,bool closed)
    {
        Check(plan.Why==null,"fillet rejected: "+plan.Why);
        Check(MeshPlans.Check(points,faces,plan)==null,"invalid fillet: "+MeshPlans.Check(points,faces,plan));
        var all=points.Concat(plan.Points.Select(p=>p.P)).ToArray();
        var result=faces.Where((_,i)=>!plan.Remove.Contains(i)).Concat(plan.Add.Select(f=>f.Corners)).ToList();
        Check(all.All(p=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z)),"finite geometry");
        Check(plan.Add.All(f=>f.Corners.Length is 3 or 4),"game accepts only triangles and quads");
        foreach(var edge in result.SelectMany(Sides).GroupBy(e=>FaceMerge.Key(e.A,e.B)))
        {
            Check(edge.Count()<3&&(!closed||edge.Count()==2),"fillet is manifold and keeps the original closed shell");
            if(edge.Count()==2) Check(edge.First().A==edge.Last().B,"shared edges face opposite directions");
        }
        Check(Enumerable.Range(points.Count,plan.Points.Count).All(v=>result.Any(f=>f.Contains(v))),"all new points are used");
        Check(plan.Points.All(p=>p.Blend.All(b=>b.V>=0&&b.V<points.Count)&&Math.Abs(p.Blend.Sum(b=>b.W)-1)<1e-6),"armour interpolation refers to original corners");
    }

    static (List<Vector3> P,List<int[]> F) Prism(IReadOnlyList<Vector2> polygon,float shear=0)
    {
        int n=polygon.Count;
        var p=polygon.Select(v=>new Vector3(v,0)).Concat(polygon.Select(v=>new Vector3(v,2))).ToList();
        if(shear!=0) p=p.Select(v=>v+new Vector3(0,0,shear*v.X)).ToList();
        var f=new List<int[]> { Enumerable.Range(0,n).Reverse().ToArray(),Enumerable.Range(n,n).ToArray() };
        for(int i=0;i<n;i++) f.Add(new[]{i,(i+1)%n,(i+1)%n+n,i+n});
        return(p,f);
    }
    internal static void Run()
    {
        checks=0;
        foreach(float degrees in new[]{30f,60,90,120,150})
        {
            float theta=degrees*MathF.PI/180, radius=.1f;
            var (p,f)=Prism(new[]{Vector2.Zero,new Vector2(2,0),new Vector2(2*MathF.Cos(theta),2*MathF.Sin(theta))});
            Vector3 centre=new(radius/MathF.Tan(theta/2),radius,0);
            foreach(int segments in new[]{2,4,8,16})
            {
                var before=p.ToArray();
                var plan=EdgeFillet.Round(p,f,new[]{(0,3)},radius,segments);
                Check(plan.Why==null,$"angle {degrees}, segments {segments}: {plan.Why}");
                Valid(p,f,plan,true);
                Check(p.SequenceEqual(before),"planning never changes the original mesh");
                var arc=plan.Points.Where(v=>v.Blend.Length==1&&v.Blend[0].V==0).Select(v=>v.P).ToArray();
                Check(arc.Length==segments+1,"segment count produces exactly the requested profile points");
                foreach(var point in arc)
                {
                    Check(Math.Abs(Vector3.Distance(point,centre)-radius)<2e-6,"constant physical radius on a non-right-angle corner");
                    Check(Math.Abs(point.Z)<1e-6,"end arc remains on the original cap plane");
                }
                var tangent1=arc.OrderBy(v=>Math.Abs(v.Y)).First();
                var ray=new Vector3(MathF.Cos(theta),MathF.Sin(theta),0);
                var tangent2=arc.OrderBy(v=>Vector3.Cross(v,ray).Length()).First();
                Check(Math.Abs(Vector3.Dot(tangent1-centre,Vector3.UnitX))<2e-6,"first radius is perpendicular to its face tangent");
                Check(Math.Abs(Vector3.Dot(tangent2-centre,ray))<2e-6,"second radius is perpendicular to its face tangent");
                Check(Math.Abs(tangent1.X-radius/MathF.Tan(theta/2))<2e-6,"setback uses the corner angle and radius");
                // Measure from the first face's inward normal; this avoids wrapping at +/- pi.
                var angles=arc.Select(v=>MathF.Atan2((v-centre).X,-(v-centre).Y)).OrderBy(v=>v).ToArray();
                float step=angles[1]-angles[0];
                for(int i=2;i<angles.Length;i++) Check(Math.Abs(angles[i]-angles[i-1]-step)<2e-5,"segments are spaced evenly in angle");
                var selected=new HashSet<(int,int)>{(0,3)};
                var sources=BevelEdges.Sources(p,f,plan,selected);
                Check(sources.All(s=>!selected.Contains(s.Value)),"rounded edges do not inherit the removed sharp-edge flags");
            }
        }

        // Sloping end caps must intersect a cylinder without stretching the radius.
        {
            var (p,f)=Prism(new[]{Vector2.Zero,new Vector2(2,0),new Vector2(0,2)},.4f);
            var plan=EdgeFillet.Round(p,f,new[]{(0,3)},.1f,8);
            Valid(p,f,plan,true);
            foreach(var np in plan.Points.Where(v=>v.Blend.Length==1&&v.Blend[0].V==0))
            {
                Check(Math.Abs(MathF.Sqrt((np.P.X-.1f)*(np.P.X-.1f)+(np.P.Y-.1f)*(np.P.Y-.1f))-.1f)<2e-6,"oblique cap keeps the cylinder radius");
                Check(Math.Abs(np.P.Z-.4f*np.P.X)<2e-6,"oblique cap points stay on the end face");
            }
        }

        // Cube chains, closed loops and three-edge corners share all profile boundary points.
        var cube=Enumerable.Range(0,8).Select(i=>new Vector3(i&1,(i>>1)&1,(i>>2)&1)).ToList();
        var faces=new List<int[]> {new[]{0,1,5,4},new[]{2,3,7,6},new[]{0,2,6,4},new[]{1,3,7,5},new[]{0,1,3,2},new[]{4,5,7,6}};
        faces=faces.Select(f=>Vector3.Dot(HoleRing.Normal(f.Select(v=>cube[v]).ToList()),f.Aggregate(Vector3.Zero,(s,v)=>s+cube[v])/4-new Vector3(.5f))<0?f.Reverse().ToArray():f).ToList();
        var edges=faces.SelectMany(Sides).Select(e=>FaceMerge.Key(e.A,e.B)).Distinct().ToArray();
        foreach(var selected in new[]{new[]{(2,3)},new[]{(2,3),(3,7)},new[]{(3,7),(5,7),(6,7)},new[]{(2,3),(3,7),(7,6),(6,2)},edges})
            foreach(int segments in new[]{2,4,8,16}) Valid(cube,faces,EdgeFillet.Round(cube,faces,selected,.1f,segments),true);

        // A concave inside corner gets a tangent round that fills the inside of the corner.
        {
            var (p,f)=Prism(new[]{new Vector2(0,0),new(2,0),new(2,1),new(1,1),new(1,2),new(0,2)});
            var plan=EdgeFillet.Round(p,f,new[]{(3,9)},.1f,8);
            Valid(p,f,plan,true);
            var centre=new Vector3(1.1f,1.1f,0);
            foreach(var np in plan.Points.Where(v=>v.Blend.Length==1&&v.Blend[0].V==3)) Check(Math.Abs(Vector3.Distance(np.P,centre)-.1f)<2e-6,"concave corner also has a constant-radius profile");
        }

        var quat=Quaternion.CreateFromYawPitchRoll(.7f,.3f,-.2f);
        var rotated=cube.Select(v=>Vector3.Transform(v,quat)+new Vector3(10,-2,5)).ToList();
        var originalPlan=EdgeFillet.Round(cube,faces,new[]{(2,3),(3,7)},.1f,8);
        var rotatedPlan=EdgeFillet.Round(rotated,faces,new[]{(2,3),(3,7)},.1f,8);
        Valid(rotated,faces,rotatedPlan,true);
        Check(rotatedPlan.Points.Count==originalPlan.Points.Count&&rotatedPlan.Points.Zip(originalPlan.Points).All(pair=>Vector3.Distance(pair.First.P,Vector3.Transform(pair.Second.P,quat)+new Vector3(10,-2,5))<1e-5),"fillet is independent of world orientation");
        var reflected=cube.Select(v=>new Vector3(-v.X,v.Y,v.Z)).ToList();
        var reflectedFaces=faces.Select(f=>f.Reverse().ToArray()).ToList();
        Valid(reflected,reflectedFaces,EdgeFillet.Round(reflected,reflectedFaces,new[]{(2,3)},.1f,8),true);
        var duplicated=EdgeFillet.Round(cube,faces,new[]{(2,3),(3,2),(2,3)},.1f,8);
        var single=EdgeFillet.Round(cube,faces,new[]{(2,3)},.1f,8);
        Check(duplicated.Points.Select(v=>v.P).SequenceEqual(single.Points.Select(v=>v.P))&&duplicated.Add.Count==single.Add.Count,"duplicate reversed selections produce one fillet");

        void Reject(MeshPlans.Rebuild plan,string message)
        {
            Check(plan.Why!=null,message);
            Check(plan.Remove.Count==0&&plan.Add.Count==0&&plan.Points.Count==0,"failed fillet returns no partial edits");
        }
        foreach(float radius in new[]{0f,-.1f,float.NaN,float.PositiveInfinity,.6f}) Reject(EdgeFillet.Round(cube,faces,new[]{(2,3)},radius,4),"invalid or oversized radius rejected");
        foreach(int segments in new[]{1,17,0}) Reject(EdgeFillet.Round(cube,faces,new[]{(2,3)},.1f,segments),"invalid segments rejected");
        Reject(EdgeFillet.Round(cube,faces,Array.Empty<(int,int)>(),.1f,4),"empty selection rejected");
        Reject(EdgeFillet.Round(cube,faces,new[]{(0,7)},.1f,4),"non-edge rejected");
        Reject(EdgeFillet.Round(cube,new[]{faces[0]},new[]{(0,1)},.1f,4),"open edge rejected");
        var crowd=faces.Append(faces.First(f=>f.Contains(2)&&f.Contains(3))).ToList();
        Reject(EdgeFillet.Round(cube,crowd,new[]{(2,3)},.1f,4),"non-manifold edge rejected");
        var reversed=faces.Select((f,i)=>i==1?f.Reverse().ToArray():f).ToList();
        Reject(EdgeFillet.Round(cube,reversed,new[]{(2,3)},.1f,4),"inconsistent face winding rejected");
        var flat=new List<Vector3>{new(0,0,0),new(1,0,0),new(1,1,0),new(0,1,0)};
        Reject(EdgeFillet.Round(flat,new[]{new[]{0,1,2},new[]{0,2,3}},new[]{(0,2)},.1f,4),"coplanar edge rejected");
        var warped=cube.ToList(); warped[7]+=new Vector3(0,.03f,0);
        Reject(EdgeFillet.Round(warped,faces,new[]{(2,3)},.1f,4),"warped neighbouring face rejected");
        Console.WriteLine($"FILLET_TESTS_OK: {checks} checks");
    }
}
