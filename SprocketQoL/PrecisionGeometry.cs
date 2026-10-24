using System.Numerics;
using static SprocketQoL.MeshPlans;

namespace SprocketQoL;

/// Selection-scale geometry operations. Plans never mutate their supplied mesh.
public static class PrecisionGeometry
{
    public enum FalloffShape { Smooth, Linear, Sharp, Constant }
    public sealed record Health(int Vertices, int Edges, int Faces, int OpenEdges, int NonManifoldEdges,
        int DuplicatePoints, int DegenerateFaces, int InvalidFaces, int[] ProblemVertices, int[] ProblemFaces);
    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    static (int,int) Key(int a,int b) => a < b ? (a,b) : (b,a);
    public static Dictionary<int,Vector3> Align(IReadOnlyList<Vector3> points,ICollection<int> selected,Vector3 from,Vector3 to)
    {
        var ids=selected.Distinct().ToArray();
        if(ids.Length==0||ids.Any(i=>i<0||i>=points.Count||!Finite(points[i]))||!Finite(from)||!Finite(to)||from.LengthSquared()<1e-12f||to.LengthSquared()<1e-12f)return new();
        from=Vector3.Normalize(from);to=Vector3.Normalize(to);
        if(Vector3.Dot(from,to)<0)to=-to; // Parallel lines/planes use the smaller rotation.
        float dot=Math.Clamp(Vector3.Dot(from,to),-1,1);var axis=Vector3.Cross(from,to);
        if(axis.LengthSquared()<1e-14f)return new();
        var rotation=Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),MathF.Acos(dot));
        var pivot=ids.Select(i=>points[i]).Aggregate(Vector3.Zero,(a,b)=>a+b)/ids.Length;
        return ids.ToDictionary(i=>i,i=>pivot+Vector3.Transform(points[i]-pivot,rotation));
    }
    public static float Clearance(IReadOnlyList<Vector3> a,IReadOnlyList<Vector3> b)
    {
        if(a.Count<3||b.Count<3||a.Concat(b).Any(p=>!Finite(p))) return float.NaN;
        Vector3 Normal(IReadOnlyList<Vector3> f)=>Vector3.Normalize(HoleRing.Normal(f));
        var na=Normal(a);var nb=Normal(b);
        if(!Finite(na)||!Finite(nb)||a.Any(p=>Math.Abs(Vector3.Dot(p-a[0],na))>.0001f)||b.Any(p=>Math.Abs(Vector3.Dot(p-b[0],nb))>.0001f))return float.NaN;
        bool Inside(Vector3 p,IReadOnlyList<Vector3> face,Vector3 n)
        {
            Vector2 Project(Vector3 v)=>Math.Abs(n.X)>Math.Abs(n.Y)&&Math.Abs(n.X)>Math.Abs(n.Z)?new(v.Y,v.Z):Math.Abs(n.Y)>Math.Abs(n.Z)?new(v.X,v.Z):new(v.X,v.Y);
            var q=Project(p);bool inside=false;
            for(int i=0,j=face.Count-1;i<face.Count;j=i++)
            {
                var x=Project(face[i]);var y=Project(face[j]);
                if((x.Y>q.Y)!=(y.Y>q.Y)&&q.X<(y.X-x.X)*(q.Y-x.Y)/(y.Y-x.Y)+x.X)inside=!inside;
            }
            return inside;
        }
        float Segments(Vector3 p,Vector3 q,Vector3 r,Vector3 s)
        {
            var u=q-p;var v=s-r;var w=p-r;float aa=Vector3.Dot(u,u),bb=Vector3.Dot(u,v),cc=Vector3.Dot(v,v),dd=Vector3.Dot(u,w),ee=Vector3.Dot(v,w);
            if(aa<1e-20f||cc<1e-20f)return float.PositiveInfinity;
            float den=aa*cc-bb*bb;
            float t=den>1e-20f?Math.Clamp((bb*ee-cc*dd)/den,0,1):0;
            float h=(bb*t+ee)/cc;
            if(h<0){h=0;t=Math.Clamp(-dd/aa,0,1);}else if(h>1){h=1;t=Math.Clamp((bb-dd)/aa,0,1);}
            return Vector3.Distance(p+t*u,r+h*v);
        }
        float best=float.PositiveInfinity;
        void Probe(IReadOnlyList<Vector3> from,IReadOnlyList<Vector3> to,Vector3 normal)
        {
            for(int i=0;i<from.Count;i++)
            {
                var p=from[i];var end=from[(i+1)%from.Count];float d=Vector3.Dot(p-to[0],normal);var projected=p-d*normal;
                if(Inside(projected,to,normal))best=Math.Min(best,Math.Abs(d));
                float next=Vector3.Dot(end-to[0],normal);
                if(d*next<0&&Inside(p+(end-p)*(d/(d-next)),to,normal))best=0;
                for(int j=0;j<to.Count;j++)best=Math.Min(best,Segments(p,end,to[j],to[(j+1)%to.Count]));
            }
        }
        Probe(a,b,nb);Probe(b,a,na);return best;
    }
    public static Rebuild WeldDuplicates(IReadOnlyList<Vector3> points, IReadOnlyList<int[]> faces, ICollection<int> selected, float tolerance = .00001f)
    {
        var ids = selected.Distinct().OrderBy(i => i).ToArray();
        if (ids.Length < 2 || ids.Any(i => i < 0 || i >= points.Count || !Finite(points[i]))) return Rebuild.Fail("Select at least two finite points.");
        if (ids.Length > 4096) return Rebuild.Fail("Weld at most 4,096 selected points at a time.");
        var map = ids.ToDictionary(i => i, i => i);
        foreach (int i in ids) foreach (int j in ids.TakeWhile(j => j < i))
            if (Vector3.DistanceSquared(points[i], points[j]) <= tolerance*tolerance) { map[i] = map[j]; break; }
        var removed = new List<int>(); var added = new List<NewFace>();
        for (int f = 0; f < faces.Count; f++)
        {
            var original = faces[f]; var corners = original.Select(i => map.TryGetValue(i, out int next) ? next : i).ToArray();
            if (original.SequenceEqual(corners)) continue;
            if (corners.Length < 3 || corners.Distinct().Count() != corners.Length)
                return Rebuild.Fail("Welding would collapse a face. Deselect adjacent corners and inspect the mesh.");
            removed.Add(f); added.Add(new(corners,f));
        }
        if (removed.Count == 0) return Rebuild.Fail("No selected duplicate face points within 0.01 mm.");
        var plan = new Rebuild(removed, added, new(), null);
        if (MeshPlans.Check(points, faces, plan, false) is string why) return plan with { Why = why };
        return plan;
    }
    public static Dictionary<int,List<int>> Neighbours(int count, IEnumerable<int[]> faces)
    {
        var result=Enumerable.Range(0,count).ToDictionary(i=>i,_=>new List<int>());
        foreach(var f in faces) for(int k=0;k<f.Length;k++)
        {
            int a=f[k],b=f[(k+1)%f.Length];
            if(a<0||b<0||a>=count||b>=count||a==b) continue;
            if(!result[a].Contains(b)) result[a].Add(b);
            if(!result[b].Contains(a)) result[b].Add(a);
        }
        return result;
    }
    public static Dictionary<int,(int[] Moved,float[] Weights)> Influence(IReadOnlyList<Vector3> p,
        IReadOnlyList<int[]> faces, ICollection<int> selected,float radius,bool connected,bool topologyDistance,FalloffShape shape)
    {
        var result=new Dictionary<int,(int[],float[])>();
        if(!float.IsFinite(radius)||radius<=0||selected.Count==0) return result;
        var seeds=selected.Distinct().Where(v=>v>=0&&v<p.Count&&Finite(p[v])).ToArray();
        if(seeds.Length==0) return result;
        var graph=Neighbours(p.Count,faces);
        var component=Enumerable.Repeat(-1,p.Count).ToArray();int componentId=0;
        for(int start=0;start<p.Count;start++) if(component[start]<0)
        {
            var connectedQueue=new Queue<int>();connectedQueue.Enqueue(start);component[start]=componentId;
            while(connectedQueue.TryDequeue(out int current)) foreach(int neighbour in graph[current])
                if(component[neighbour]<0) { component[neighbour]=componentId;connectedQueue.Enqueue(neighbour); }
            componentId++;
        }
        var allowed=new HashSet<int>(seeds);var queue=new Queue<int>(seeds);
        while(queue.TryDequeue(out int v)) foreach(int n in graph[v]) if(allowed.Add(n)) queue.Enqueue(n);
        // Multi-source geodesic distance gates influence without crossing disconnected shells.
        var distance=Enumerable.Repeat(float.PositiveInfinity,p.Count).ToArray();
        var frontier=new PriorityQueue<int,float>();
        foreach(int s in seeds) { distance[s]=0;frontier.Enqueue(s,0); }
        if(topologyDistance) while(frontier.TryDequeue(out int v,out float d))
        {
            if(d>distance[v]||d>=radius) continue;
            foreach(int n in graph[v])
            {
                float next=d+Vector3.Distance(p[v],p[n]);
                if(float.IsFinite(next)&&next<distance[n]&&next<radius) { distance[n]=next;frontier.Enqueue(n,next); }
            }
        }
        var moved=seeds.ToHashSet();
        // Seeds by radius-sized cell: each point looks at the 27 cells round it, not at every seed (each point against
        // each seed was slow with many of both). Kept in the seeds' order, so the answer is the same.
        (int,int,int) Cell(Vector3 q)=>((int)MathF.Floor(q.X/radius),(int)MathF.Floor(q.Y/radius),(int)MathF.Floor(q.Z/radius));
        var cells=new Dictionary<(int,int,int),List<int>>();
        for(int i=0;i<seeds.Length;i++) { var c=Cell(p[seeds[i]]);(cells.TryGetValue(c,out var list)?list:cells[c]=new()).Add(i); }
        var around=new List<int>();
        for(int v=0;v<p.Count;v++)
        {
            if(moved.Contains(v)||!Finite(p[v])||(connected||topologyDistance)&&!allowed.Contains(v)) continue;
            var (cx,cy,cz)=Cell(p[v]);around.Clear();
            for(int dx=-1;dx<=1;dx++) for(int dy=-1;dy<=1;dy++) for(int dz=-1;dz<=1;dz++)
                if(cells.TryGetValue((cx+dx,cy+dy,cz+dz),out var here)) around.AddRange(here);
            if(around.Count==0) continue;
            around.Sort();
            var near=around.Select(i=>seeds[i]).Where(m=>!(connected||topologyDistance)||component[m]==component[v])
                .Select(m=>(M:m,D:Vector3.Distance(p[v],p[m]))).Where(x=>x.D<radius).ToArray();
            if(near.Length==0) continue;
            float d=topologyDistance?distance[v]:near.Min(n=>n.D);
            if(d>=radius) continue;
            float x=Math.Clamp(d/radius,0,1);
            float fall=shape switch { FalloffShape.Linear=>1-x,FalloffShape.Sharp=>(1-x)*(1-x),FalloffShape.Constant=>1,_=>1-x*x*(3-2*x) };
            var weights=near.Select(n=>1/(n.D*n.D+1e-8f)).ToArray();float sum=weights.Sum();
            result[v]=(near.Select(n=>n.M).ToArray(),weights.Select(w=>fall*w/sum).ToArray());
        }
        return result;
    }
    /// Slide selected vertices toward one connected, unselected rail per vertex. The first rail establishes a side.
    public static Dictionary<int,Vector3> Slide(IReadOnlyList<Vector3> p,IReadOnlyList<int[]> faces,
        ICollection<int> selected,float amount,bool millimetres,bool opposite,Func<Vector3,Vector3>? metric=null)
    {
        var moved=new Dictionary<int,Vector3>();
        if(!float.IsFinite(amount)||amount==0) return moved;
        var set=selected.Where(v=>v>=0&&v<p.Count).ToHashSet();var graph=Neighbours(p.Count,faces);
        int seed=set.OrderBy(value => value).FirstOrDefault(v=>graph[v].Any(n=>!set.Contains(n)),-1);
        if(seed<0) return moved;
        var rails=graph[seed].Where(n=>!set.Contains(n)).OrderBy(value => value).ToArray();
        var direction=p[rails[opposite&&rails.Length>1?1:0]]-p[seed];
        if(direction.LengthSquared()<1e-12f) return moved;
        direction=Vector3.Normalize(direction);
        foreach(int v in set)
        {
            var candidates=graph[v].Where(n=>!set.Contains(n)&&Vector3.DistanceSquared(p[v],p[n])>1e-12f).ToArray();
            if(candidates.Length==0) return new();
            int rail=candidates.OrderByDescending(n=>Vector3.Dot(Vector3.Normalize(p[n]-p[v]),direction)).First();
            float length=metric==null?Vector3.Distance(p[v],p[rail]):Vector3.Distance(metric(p[v]),metric(p[rail]));
            float t=millimetres?amount/1000/length:amount/100;
            if(!float.IsFinite(t)||t<=0||t>=1) return new(); // Never collapse or extrapolate a plate.
            moved[v]=Vector3.Lerp(p[v],p[rail],t);
        }
        return moved;
    }
    public static Dictionary<int,Vector3> SetDistance(IReadOnlyList<Vector3> p,int anchor,int end,float metres)
    {
        if(anchor<0||end<0||anchor>=p.Count||end>=p.Count||anchor==end||!float.IsFinite(metres)||metres<=0) return new();
        var direction=p[end]-p[anchor];
        if(direction.LengthSquared()<1e-12f) return new();
        return new() { [end]=p[anchor]+Vector3.Normalize(direction)*metres };
    }
    public static Health Inspect(IReadOnlyList<Vector3> p,IReadOnlyList<int[]> faces,float tolerance=.000001f)
    {
        var badVertices=new HashSet<int>();var badFaces=new HashSet<int>();var edges=new Dictionary<(int,int),int>();
        var buckets=new Dictionary<(long,long,long),List<int>>();int duplicates=0,degenerate=0,invalid=0;
        tolerance=float.IsFinite(tolerance)&&tolerance>0?tolerance:.000001f;
        for(int i=0;i<p.Count;i++)
        {
            if(!Finite(p[i])||Math.Abs(p[i].X/tolerance)>long.MaxValue/4d||Math.Abs(p[i].Y/tolerance)>long.MaxValue/4d||Math.Abs(p[i].Z/tolerance)>long.MaxValue/4d)
            { badVertices.Add(i);continue; }
            var key=((long)Math.Floor(p[i].X/tolerance),(long)Math.Floor(p[i].Y/tolerance),(long)Math.Floor(p[i].Z/tolerance));
            bool duplicate=false;
            for(int x=-1;x<=1;x++) for(int y=-1;y<=1;y++) for(int z=-1;z<=1;z++)
                if(buckets.TryGetValue((key.Item1+x,key.Item2+y,key.Item3+z),out var near))
                    foreach(int other in near) if(Vector3.DistanceSquared(p[i],p[other])<=tolerance*tolerance) { duplicate=true;badVertices.Add(other); }
            if(duplicate) { duplicates++;badVertices.Add(i); }
            if(!buckets.TryGetValue(key,out var own)) buckets[key]=own=new();own.Add(i);
        }
        for(int i=0;i<faces.Count;i++)
        {
            var f=faces[i];
            if(f.Length<3||f.Any(v=>v<0||v>=p.Count||!Finite(p[v]))||f.Distinct().Count()!=f.Length)
            { invalid++;badFaces.Add(i);continue; }
            if(HoleRing.Normal(f.Select(v=>p[v]).ToArray()).LengthSquared()<1e-16f) { degenerate++;badFaces.Add(i); }
            for(int k=0;k<f.Length;k++) { var e=Key(f[k],f[(k+1)%f.Length]);edges[e]=edges.GetValueOrDefault(e)+1; }
        }
        foreach(var e in edges.Where(e=>e.Value>2)) { badVertices.Add(e.Key.Item1);badVertices.Add(e.Key.Item2); }
        return new(p.Count,edges.Count,faces.Count,edges.Count(e=>e.Value==1),edges.Count(e=>e.Value>2),duplicates,degenerate,invalid,badVertices.OrderBy(value => value).ToArray(),badFaces.OrderBy(value => value).ToArray());
    }
    public static Rebuild LoopCuts(IReadOnlyList<Vector3> p,IReadOnlyList<int[]> faces,IEnumerable<(int A,int B)> starts,int count,float offset)
    {
        if(count<1||count>16||!float.IsFinite(offset)||offset<=0||offset>=1) return Rebuild.Fail("use 1–16 cuts and an offset strictly between 0 and 100%");
        var uses=new Dictionary<(int,int),List<int>>();
        for(int f=0;f<faces.Count;f++) for(int k=0;k<faces[f].Length;k++)
        { var e=Key(faces[f][k],faces[f][(k+1)%faces[f].Length]);if(!uses.TryGetValue(e,out var fs)) uses[e]=fs=new();fs.Add(f); }
        var split=new Dictionary<(int,int),int[]>();var points=new List<NewPoint>();var cut=new Dictionary<int,int>();
        var fractions=Enumerable.Range(1,count).Select(i=>(i+2*(offset-.5f))/(count+1)).ToArray();
        int[] Add(int a,int b)
        {
            var e=Key(a,b);var values=fractions;
            if(!split.TryGetValue(e,out var ids))
            {
                ids=values.Select(v=> {float canonical=a==e.Item1?v:1-v;int id=p.Count+points.Count;points.Add(new(Vector3.Lerp(p[e.Item1],p[e.Item2],canonical),new[]{(e.Item1,1-canonical),(e.Item2,canonical)}));return id;}).ToArray();
                split[e]=ids;
            }
            return ids;
        }
        foreach(var (a,b) in starts)
        {
            if(!uses.TryGetValue(Key(a,b),out var first)||split.ContainsKey(Key(a,b))) continue;
            if(first.Any(cut.ContainsKey))return Rebuild.Fail("selected loop directions cross; cut one direction at a time");
            Add(a,b);
            var pending=new Queue<(int Face,int A,int B)>();foreach(int f in first) pending.Enqueue((f,a,b));
            while(pending.TryDequeue(out var entry))
            {
                if(cut.ContainsKey(entry.Face)) continue;
                var f=faces[entry.Face];int k=Array.FindIndex(f,v=>v==entry.A);
                if(k<0) return Rebuild.Fail("invalid loop connection");
                if(f[(k+1)%f.Length]!=entry.B) k=Array.IndexOf(f,entry.B);
                if(f.Length is not (3 or 4)) return Rebuild.Fail("loop cuts require triangles or quads");
                cut[entry.Face]=k;
                if(f.Length!=4) continue;
                int x=f[(k+3)%4],y=f[(k+2)%4];
                // Project existing entry positions onto its directed edge to preserve orientation through the ring.
                var edge=Key(f[k],f[(k+1)%4]);var entryIds=split[edge];
                float d=Vector3.DistanceSquared(p[f[k]],p[f[(k+1)%4]]);
                if(d<1e-12f) return Rebuild.Fail("the selected edge is collapsed");
                var ts=entryIds.Select(id=>Vector3.Dot(points[id-p.Count].P-p[f[k]],p[f[(k+1)%4]]-p[f[k]])/d).OrderBy(value => value).ToArray();
                var next=Key(x,y);
                if(!split.ContainsKey(next))
                    split[next]=ts.Select(t=>{int id=p.Count+points.Count;points.Add(new(Vector3.Lerp(p[x],p[y],t),new[]{(x,1-t),(y,t)}));return id;}).ToArray();
                foreach(int g in uses[next].Where(g=>g!=entry.Face)) pending.Enqueue((g,x,y));
            }
        }
        if(cut.Count==0) return Rebuild.Fail("select an edge of a face");
        foreach(var (edge,fs) in uses) if(split.ContainsKey(edge)) foreach(int f in fs)
            if(!cut.ContainsKey(f)) return Rebuild.Fail("the cut would leave a disconnected border");
        var added=new List<NewFace>();
        int[] Along(int a,int b) => split[Key(a,b)].OrderBy(id=>Vector3.Dot(points[id-p.Count].P-p[a],p[b]-p[a])).ToArray();
        foreach(var (fi,data) in cut)
        {
            var f=faces[fi];int k=data;var left=new[]{f[k]}.Concat(Along(f[k],f[(k+1)%f.Length])).Append(f[(k+1)%f.Length]).ToArray();
            if(f.Length==3) for(int i=0;i+1<left.Length;i++) added.Add(new(new[]{left[i],left[i+1],f[(k+2)%3]},fi));
            else
            {
                var right=new[]{f[(k+3)%4]}.Concat(Along(f[(k+3)%4],f[(k+2)%4])).Append(f[(k+2)%4]).ToArray();
                for(int i=0;i+1<left.Length;i++) added.Add(new(new[]{left[i],left[i+1],right[i+1],right[i]},fi));
            }
        }
        return new(cut.Keys.ToList(),added,points,null);
    }
}
