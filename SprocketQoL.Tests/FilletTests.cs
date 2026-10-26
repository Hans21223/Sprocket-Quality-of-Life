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

    // Topology alone accepts a closed but dented corner. A box filleted on all three incident
    // edges has an independent analytic answer: one octant of a sphere, tangent to the strips.
    static void RoundedCubeJunctions(List<Vector3> cube,List<int[]> faces,(int A,int B)[] selected,
        float radius,int segments,IReadOnlyList<int> junctions,bool wholeBox)
    {
        var before=cube.ToArray();
        var plan=EdgeFillet.Round(cube,faces,selected,radius,segments);
        string context=$"cube {(wholeBox?"all edges":"three edges")}, r={radius}, n={segments}: ";
        Check(plan.Why==null,context+plan.Why);
        Valid(cube,faces,plan,true);
        Check(cube.SequenceEqual(before),context+"corner generation leaves the input untouched");
        var at=cube.Concat(plan.Points.Select(p=>p.P)).ToArray();
        var result=faces.Where((_,i)=>!plan.Remove.Contains(i)).Concat(plan.Add.Select(f=>f.Corners)).ToArray();
        var uses=result.SelectMany((f,i)=>Sides(f).Select(e=>(Key:FaceMerge.Key(e.A,e.B),Face:i)))
            .GroupBy(e=>e.Key).ToDictionary(g=>g.Key,g=>g.Select(e=>e.Face).ToArray());
        var vertices=result.SelectMany(f=>f).Distinct().ToArray();
        Check(vertices.Length-uses.Count+result.Length==2,context+"rounded cube remains a single closed shell");
        float tolerance=Math.Max(2e-6f,radius*2e-5f);
        Vector3 FaceNormal(int[] f)
        {
            Vector3 sum=Vector3.Zero;
            for(int i=1;i+1<f.Length;i++) sum+=Vector3.Cross(at[f[i]]-at[f[0]],at[f[i+1]]-at[f[0]]);
            return Vector3.Normalize(sum);
        }
        var faceNormals=result.Select(FaceNormal).ToArray();
        foreach(var f in result.Where(f=>f.Length==4))
        {
            Vector3 normal=Vector3.Normalize(Vector3.Cross(at[f[1]]-at[f[0]],at[f[2]]-at[f[0]]));
            Check(Math.Abs(Vector3.Dot(at[f[3]]-at[f[0]],normal))<tolerance,
                context+"strip quads are planar and cannot make diagonal corner notches");
        }
        if(wholeBox)
        {
            Check(BevelEdges.Sources(cube,faces,plan,selected.ToHashSet()).Count==0,
                context+"rounded strips and corner patches cannot inherit removed sharp-edge flags");
            foreach(int id in vertices)
            {
                Vector3 p=at[id],nearest=Vector3.Clamp(p,new Vector3(radius),new Vector3(1-radius));
                Check(Math.Abs(Vector3.Distance(p,nearest)-radius)<tolerance,
                    context+"every surface point follows the rounded-box equation");
            }
        }
        foreach(int u in junctions)
        {
            var original=cube[u];
            var centre=new Vector3(original.X<.5f?radius:1-radius,original.Y<.5f?radius:1-radius,original.Z<.5f?radius:1-radius);
            var outward=2*original-Vector3.One;
            var ids=plan.Points.Select((p,i)=>(Point:p,Id:cube.Count+i))
                .Where(p=>p.Point.Blend.Length==1&&p.Point.Blend[0].V==u).Select(p=>p.Id).ToHashSet();
            Check(ids.Count>=3*segments,context+"the spherical junction retains each strip's sampled boundary");
            foreach(int id in ids)
            {
                var p=at[id];
                Check(Math.Abs(Vector3.Distance(p,centre)-radius)<tolerance,
                    context+$"corner {u} has a true spherical radius, without an averaged centre dent");
                Check(Vector3.Min((p-centre)*outward,Vector3.Zero).Length()<tolerance,
                    context+"sphere points stay in the outward corner octant");
            }
            var capFaces=result.Select((f,i)=>(Corners:f,Id:i)).Where(f=>f.Corners.All(ids.Contains)).ToArray();
            Check(capFaces.Length>0,context+"the corner is filled with an actual surface patch");
            var capIds=capFaces.Select(f=>f.Id).ToHashSet();
            var capEdges=capFaces.SelectMany(f=>Sides(f.Corners)).Select(e=>FaceMerge.Key(e.A,e.B)).Distinct().ToArray();
            float maxAngle=MathF.PI/segments+2e-4f;
            foreach(var e in capEdges)
            {
                Vector3 a=Vector3.Normalize(at[e.Item1]-centre),b=Vector3.Normalize(at[e.Item2]-centre);
                Check(MathF.Acos(Math.Clamp(Vector3.Dot(a,b),-1,1))<=maxAngle,
                    context+"corner patch resolution grows with the edge segments, without a fixed centre fan");
                var neighbours=uses[e];
                Check(neighbours.Length==2,context+"every patch edge shares exactly two indexed faces");
                if(neighbours.Count(capIds.Contains)==1)
                    Check(MathF.Acos(Math.Clamp(Vector3.Dot(faceNormals[neighbours[0]],faceNormals[neighbours[1]]),-1,1))<=maxAngle,
                        context+"corner normals meet the cylinder strips with only the requested tessellation error");
            }
            var boundary=capEdges.Where(e=>uses[e].Count(capIds.Contains)==1).ToArray();
            Check(boundary.Length==3*segments,context+"all three complete strip boundaries are welded to the patch");
            foreach(var f in capFaces)
            {
                Vector3 centroid=f.Corners.Aggregate(Vector3.Zero,(sum,id)=>sum+at[id])/f.Corners.Length;
                Vector3 radial=Vector3.Normalize(centroid-centre);
                Check(Vector3.Dot(faceNormals[f.Id],radial)>.9f,
                    context+"corner faces point outwards instead of folding back into the cube");
                Check(Vector3.Distance(centroid,centre)>=radius*MathF.Cos(MathF.PI/segments)-tolerance,
                    context+"facet sagitta follows the requested resolution, without broad flat triangles");
            }
        }
    }

    static MeshPlans.Rebuild SupportedCube(List<Vector3> cube,List<int[]> faces,(int A,int B)[] edges,
        float radius,int segments,float gap)
    {
        var original=cube.ToArray();
        var plan=EdgeFillet.Round(cube,faces,edges,radius,segments,gap);
        string context=$"support loops r={radius}, n={segments}, gap={gap}: ";
        Check(plan.Why==null,context+plan.Why);
        Valid(cube,faces,plan,true);
        Check(cube.SequenceEqual(original),context+"planning support loops never edits the input mesh");
        var at=cube.Concat(plan.Points.Select(p=>p.P)).ToArray();
        var result=faces.Where((_,i)=>!plan.Remove.Contains(i)).Concat(plan.Add.Select(f=>f.Corners)).ToArray();
        var vertices=result.SelectMany(f=>f).Distinct().ToArray();
        var uses=result.SelectMany((f,i)=>Sides(f).Select(e=>(Key:FaceMerge.Key(e.A,e.B),Face:i)))
            .GroupBy(e=>e.Key).ToDictionary(g=>g.Key,g=>g.Select(e=>e.Face).ToArray());
        Check(vertices.Length-uses.Count+result.Length==2,context+"support loops keep one closed shell");
        const float tolerance=3e-6f;
        var planarInterpolation=plan.Points.Where(p=>p.Blend.Length>1).ToArray();
        Check(planarInterpolation.Length>=24,context+"planar control loops have interpolated original-face attributes");
        foreach(var p in planarInterpolation)
            Check(Vector3.Distance(p.P,p.Blend.Aggregate(Vector3.Zero,(sum,b)=>sum+cube[b.V]*b.W))<tolerance,
                context+"control-loop armour interpolation follows its actual position on the original plate");
        foreach(int id in vertices)
        {
            Vector3 p=at[id],nearest=Vector3.Clamp(p,new Vector3(radius),new Vector3(1-radius));
            Check(Math.Abs(Vector3.Distance(p,nearest)-radius)<tolerance,
                context+"control edges preserve the analytic rounded-box surface and radius");
        }
        foreach(var f in result)
        {
            Vector3 normal=Vector3.Zero;
            for(int i=1;i+1<f.Length;i++) normal+=Vector3.Cross(at[f[i]]-at[f[0]],at[f[i+1]]-at[f[0]]);
            var centre=f.Aggregate(Vector3.Zero,(sum,id)=>sum+at[id])/f.Length;
            var nearest=Vector3.Clamp(centre,new Vector3(radius),new Vector3(1-radius));
            Check(Vector3.Dot(normal,centre-nearest)>0,context+"new support and corner faces point outwards");
            if(f.Length==4)
                Check(Math.Abs(Vector3.Dot(Vector3.Normalize(normal),at[f[3]]-at[f[0]]))<tolerance,
                    context+"control bands and cylindrical strips remain planar quads");
        }
        float Component(Vector3 p,int axis)=>axis==0?p.X:axis==1?p.Y:p.Z;
        int PointAt(Vector3 p)
        {
            var found=vertices.Where(id=>Vector3.Distance(at[id],p)<tolerance).ToArray();
            Check(found.Length==1,context+"control-loop corners are real, uniquely welded vertices");
            return found[0];
        }
        foreach(int axis in new[]{0,1,2}) foreach(float side in new[]{0f,1f})
        {
            int u=(axis+1)%3,v=(axis+2)%3;
            Vector3 OnFace(float x,float y)
            {
                var p=new float[3]; p[axis]=side; p[u]=x; p[v]=y;
                return new Vector3(p[0],p[1],p[2]);
            }
            float inner=radius+gap;
            var outerPoints=new[]{OnFace(radius,radius),OnFace(1-radius,radius),OnFace(1-radius,1-radius),OnFace(radius,1-radius)};
            var innerPoints=new[]{OnFace(inner,inner),OnFace(1-inner,inner),OnFace(1-inner,1-inner),OnFace(inner,1-inner)};
            var outerIds=outerPoints.Select(PointAt).ToArray();
            var innerIds=innerPoints.Select(PointAt).ToArray();
            for(int i=0;i<4;i++)
            {
                int j=(i+1)%4;
                var edge=FaceMerge.Key(innerIds[i],innerIds[j]);
                Check(uses.TryGetValue(edge,out var neighbours)&&neighbours.Length==2,
                    context+"each face has a closed inner rectangular support loop");
                Check(neighbours!.All(n=>result[n].All(id=>Math.Abs(Component(at[id],axis)-side)<tolerance)),
                    context+"support-loop neighbours stay on the original flat face");
                var band=new HashSet<int>{outerIds[i],outerIds[j],innerIds[i],innerIds[j]};
                Check(result.Any(f=>f.Length==4&&band.SetEquals(f)),
                    context+"each control edge is joined to the tangent boundary by an actual quad band");
                Vector3 along=Vector3.Normalize(innerPoints[j]-innerPoints[i]);
                Check(Math.Abs(Vector3.Cross(innerPoints[i]-outerPoints[i],along).Length()-gap)<tolerance,
                    context+"the planar control band uses a constant physical offset");
            }
        }
        // At the end of the x-directed cylinder the y/z profile is also a sphere-patch seam.
        // It must retain the ordinary arc samples and add a close sample beside each tangent.
        var profile=vertices.Where(id=>Math.Abs(at[id].X-radius)<tolerance&&at[id].Y<=radius+tolerance&&at[id].Z<=radius+tolerance)
            .Select(id=>at[id]).OrderBy(p=>MathF.Atan2(radius-p.Z,radius-p.Y)).ToArray();
        Check(profile.Length==segments+3,context+"curved control loops add actual samples at both ends of each profile");
        Check(Vector3.Distance(profile[0],profile[1])<=gap+tolerance&&Vector3.Distance(profile[^2],profile[^1])<=gap+tolerance,
            context+"curved control edges sit within the requested physical spacing of the tangent seams");
        foreach(int i in Enumerable.Range(0,segments+1))
        {
            float angle=i*MathF.PI/(2*segments);
            var expected=new Vector3(radius,radius-radius*MathF.Cos(angle),radius-radius*MathF.Sin(angle));
            Check(profile.Any(p=>Vector3.Distance(p,expected)<tolerance),
                context+"support sampling retains the original circular profile instead of changing its radius");
        }
        return plan;
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

        // The tool's default 30 mm radius and near-half-width radii, odd subdivisions, isolated
        // junctions and every box corner must all give the same tangent spherical construction.
        foreach(var (radius,segments) in new[]{(.01f,2),(.01f,4),(.01f,8),(.01f,16),(.025f,2),(.03f,2),(.03f,4),(.03f,8),(.03f,16),(.1f,2),(.1f,3),(.1f,4),(.1f,7),(.1f,8),(.1f,16),
                    (.24f,4),(.24f,8),(.24f,16),(.45f,4),(.45f,16)})
        {
            RoundedCubeJunctions(cube,faces,edges,radius,segments,Enumerable.Range(0,8).ToArray(),true);
            RoundedCubeJunctions(cube,faces,new[]{(3,7),(5,7),(6,7)},radius,segments,new[]{7},false);
        }
        foreach(int segments in new[]{2,4,8,16}) SupportedCube(cube,faces,edges,.1f,segments,.005f);
        foreach(int segments in new[]{4,8,16}) SupportedCube(cube,faces,edges,.03f,segments,.001f);

        // A small physical feature can contain small valid triangles, but its area scale must
        // never permit collapsed faces or an invalid reference to bypass the common checks.
        {
            var fine=EdgeFillet.Round(cube,faces,edges,.01f,16);
            var cap=fine.Add.First(f=>f.Corners.Length==3&&f.Corners.All(id=>id>=cube.Count
                &&fine.Points[id-cube.Count].Blend.Length==1&&fine.Points[id-cube.Count].Blend[0].V==7));
            var collapsedPoints=fine.Points.ToList();
            collapsedPoints[cap.Corners[0]-cube.Count]=collapsedPoints[cap.Corners[0]-cube.Count] with
                { P=fine.Points[cap.Corners[1]-cube.Count].P };
            Check(MeshPlans.Check(cube,faces,fine with { Points=collapsedPoints })!=null,
                "the radius-based area reference still rejects a genuinely collapsed corner triangle");
            foreach(float reference in new[]{0f,-1f,float.NaN,float.PositiveInfinity,float.MaxValue})
            {
                var invalidFaces=fine.Add.ToList();
                invalidFaces[0]=invalidFaces[0] with { ReferenceArea=reference };
                Check(MeshPlans.Check(cube,faces,fine with { Add=invalidFaces })!=null,
                    "invalid feature area references cannot disable degeneracy checks");
            }
        }

        // A concave inside corner gets a tangent round that fills the inside of the corner.
        {
            var (p,f)=Prism(new[]{new Vector2(0,0),new(2,0),new(2,1),new(1,1),new(1,2),new(0,2)});
            var plan=EdgeFillet.Round(p,f,new[]{(3,9)},.1f,8);
            Valid(p,f,plan,true);
            var centre=new Vector3(1.1f,1.1f,0);
            foreach(var np in plan.Points.Where(v=>v.Blend.Length==1&&v.Blend[0].V==3)) Check(Math.Abs(Vector3.Distance(np.P,centre)-.1f)<2e-6,"concave corner also has a constant-radius profile");
            Valid(p,f,EdgeFillet.Round(p,f,new[]{(3,9)},.1f,8,.001f),true);
            Reject(EdgeFillet.Round(p,f,new[]{(3,9)},.1f,8,.6f),"control loops reject an inset that cannot fit a concave plate");
        }

        var quat=Quaternion.CreateFromYawPitchRoll(.7f,.3f,-.2f);
        var rotated=cube.Select(v=>Vector3.Transform(v,quat)+new Vector3(10,-2,5)).ToList();
        var originalPlan=EdgeFillet.Round(cube,faces,new[]{(2,3),(3,7)},.1f,8);
        var rotatedPlan=EdgeFillet.Round(rotated,faces,new[]{(2,3),(3,7)},.1f,8);
        Valid(rotated,faces,rotatedPlan,true);
        Check(rotatedPlan.Points.Count==originalPlan.Points.Count&&rotatedPlan.Points.Zip(originalPlan.Points).All(pair=>Vector3.Distance(pair.First.P,Vector3.Transform(pair.Second.P,quat)+new Vector3(10,-2,5))<1e-5),"fillet is independent of world orientation");
        // Exercise the new three-edge spherical patches under a coordinate change as well as
        // the older two-edge chains above. Physical radius must scale with the model units.
        var roundedBox=EdgeFillet.Round(cube,faces,edges,.1f,8);
        const float boxScale=2.75f;
        var changedUnits=cube.Select(v=>boxScale*Vector3.Transform(v,quat)+new Vector3(10,-2,5)).ToList();
        var transformedBox=EdgeFillet.Round(changedUnits,faces,edges,.1f*boxScale,8);
        Valid(changedUnits,faces,transformedBox,true);
        Check(transformedBox.Points.Count==roundedBox.Points.Count&&transformedBox.Points.Zip(roundedBox.Points)
            .All(pair=>Vector3.Distance(pair.First.P,boxScale*Vector3.Transform(pair.Second.P,quat)+new Vector3(10,-2,5))<1e-5),
            "spherical junctions preserve physical radius across rotation, translation and a change of scale");
        var supportedBox=EdgeFillet.Round(cube,faces,edges,.1f,8,.005f);
        Vector3 SubdivisionStep(MeshPlans.Rebuild plan,Vector3 location)
        {
            var at=cube.Concat(plan.Points.Select(p=>p.P)).ToArray();
            var allFaces=faces.Where((_,i)=>!plan.Remove.Contains(i)).Concat(plan.Add.Select(f=>f.Corners)).ToArray();
            int id=Enumerable.Range(0,at.Length).First(i=>Vector3.Distance(at[i],location)<1e-6);
            var around=allFaces.Where(f=>f.Contains(id)).ToArray();
            var neighbours=around.SelectMany(Sides).Where(e=>e.A==id||e.B==id).Select(e=>e.A==id?e.B:e.A).Distinct().ToArray();
            var faceAverage=around.Select(f=>f.Aggregate(Vector3.Zero,(sum,v)=>sum+at[v])/f.Length).Aggregate(Vector3.Zero,(sum,p)=>sum+p)/around.Length;
            var midpointAverage=neighbours.Select(v=>(at[id]+at[v])*.5f).Aggregate(Vector3.Zero,(sum,p)=>sum+p)/neighbours.Length;
            return (faceAverage+2*midpointAverage+(neighbours.Length-3)*at[id])/neighbours.Length;
        }
        var tangentLocation=new Vector3(.1f,.1f,0);
        float originalDrift=Math.Abs(SubdivisionStep(roundedBox,tangentLocation).Z);
        float supportedDrift=Math.Abs(SubdivisionStep(supportedBox,tangentLocation).Z);
        Check(originalDrift>1e-6&&supportedDrift<originalDrift*.5f,
            "real support loops reduce the tangent seam's normal drift in one Catmull-Clark subdivision step");
        var transformedSupport=EdgeFillet.Round(changedUnits,faces,edges,.1f*boxScale,8,.005f*boxScale);
        Valid(changedUnits,faces,transformedSupport,true);
        Check(transformedSupport.Points.Count==supportedBox.Points.Count&&transformedSupport.Points.Zip(supportedBox.Points)
            .All(pair=>Vector3.Distance(pair.First.P,boxScale*Vector3.Transform(pair.Second.P,quat)+new Vector3(10,-2,5))<2e-5),
            "support-loop spacing follows physical units under rotation, translation and scale");
        var oblique=cube.Select(v=>new Vector3(v.X+.2f*v.Y+.1f*v.Z,v.Y+.15f*v.Z,v.Z)).ToList();
        var obliquePlan=EdgeFillet.Round(oblique,faces,edges,.1f,8);
        Valid(oblique,faces,obliquePlan,true);
        for(int u=0;u<cube.Count;u++)
        {
            var normals=faces.Where(f=>f.Contains(u)).Select(f=>Vector3.Normalize(HoleRing.Normal(f.Select(v=>oblique[v]).ToList()))).ToArray();
            float determinant=Vector3.Dot(normals[0],Vector3.Cross(normals[1],normals[2]));
            // Three offset planes determine the tangent sphere independently of the fillet planner.
            var centre=oblique[u]-.1f/determinant*(Vector3.Cross(normals[1],normals[2])+Vector3.Cross(normals[2],normals[0])+Vector3.Cross(normals[0],normals[1]));
            foreach(var p in obliquePlan.Points.Where(p=>p.Blend.Length==1&&p.Blend[0].V==u))
                Check(Math.Abs(Vector3.Distance(p.P,centre)-.1f)<3e-6,
                    "non-right-angle three-edge junctions meet all three face offsets on one sphere");
        }
        var reflected=cube.Select(v=>new Vector3(-v.X,v.Y,v.Z)).ToList();
        var reflectedFaces=faces.Select(f=>f.Reverse().ToArray()).ToList();
        Valid(reflected,reflectedFaces,EdgeFillet.Round(reflected,reflectedFaces,new[]{(2,3)},.1f,8),true);
        var reflectedSupport=EdgeFillet.Round(reflected,reflectedFaces,edges,.1f,8,.005f);
        Valid(reflected,reflectedFaces,reflectedSupport,true);
        Check(reflectedSupport.Points.Count==supportedBox.Points.Count&&supportedBox.Points.All(p=>reflectedSupport.Points
            .Any(q=>Vector3.Distance(q.P,new Vector3(-p.P.X,p.P.Y,p.P.Z))<2e-6)),
            "mirrored support loops retain their spacing and curved shape with corrected winding");
        var duplicated=EdgeFillet.Round(cube,faces,new[]{(2,3),(3,2),(2,3)},.1f,8);
        var single=EdgeFillet.Round(cube,faces,new[]{(2,3)},.1f,8);
        Check(duplicated.Points.Select(v=>v.P).SequenceEqual(single.Points.Select(v=>v.P))&&duplicated.Add.Count==single.Add.Count,"duplicate reversed selections produce one fillet");
        foreach(var selected in new[]{new[]{(2,3)},new[]{(2,3),(3,7)},new[]{(3,7),(5,7),(6,7)},edges})
        {
            var legacy=EdgeFillet.Round(cube,faces,selected,.1f,8);
            var disabled=EdgeFillet.Round(cube,faces,selected,.1f,8,0);
            Check(legacy.Why==disabled.Why&&legacy.Remove.SequenceEqual(disabled.Remove)
                &&legacy.Points.Count==disabled.Points.Count&&legacy.Points.Zip(disabled.Points).All(p=>p.First.P==p.Second.P&&p.First.Blend.SequenceEqual(p.Second.Blend))
                &&legacy.Add.Count==disabled.Add.Count&&legacy.Add.Zip(disabled.Add).All(f=>f.First.Source==f.Second.Source
                    &&f.First.ReferenceArea==f.Second.ReferenceArea&&f.First.Corners.SequenceEqual(f.Second.Corners)),
                "disabling automatic control edges preserves the exact legacy plan");
        }

        void Reject(MeshPlans.Rebuild plan,string message)
        {
            Check(plan.Why!=null,message);
            Check(plan.Remove.Count==0&&plan.Add.Count==0&&plan.Points.Count==0,"failed fillet returns no partial edits");
        }
        foreach(float radius in new[]{0f,-.1f,float.NaN,float.PositiveInfinity,.6f}) Reject(EdgeFillet.Round(cube,faces,new[]{(2,3)},radius,4),"invalid or oversized radius rejected");
        foreach(float spacing in new[]{-.001f,float.NaN,float.PositiveInfinity,1e-7f,.4f,.45f,.5f})
            Reject(EdgeFillet.Round(cube,faces,edges,.1f,8,spacing),"invalid or oversized control-edge spacing rejected");
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
        // Select all, then Fillet: an edge between two faces in one plane has no corner and is left as it is.
        var (flatP, flatF) = Prism(new Vector2[] { new(0, 0), new(1, 0), new(2, 0), new(2, 2), new(0, 2) });
        var vertical = Enumerable.Range(0, 5).Select(i => (i, i + 5)).ToArray(); // (1, 6) lies flat between two side faces
        Valid(flatP, flatF, EdgeFillet.Round(flatP, flatF, vertical, 0.1f, 4), true);
        Check(EdgeFillet.Round(flatP, flatF, new[] { (1, 6) }, 0.1f, 4).Why == "select a corner between two distinct face planes", "only flat edges selected: nothing to round");
        Console.WriteLine($"FILLET_TESTS_OK: {checks} checks");
    }
}
