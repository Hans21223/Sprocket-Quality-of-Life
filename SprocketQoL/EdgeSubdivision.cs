using System.Numerics;

namespace SprocketQoL;

/// Straight cuts propagate to opposite quad edges. Crossing cuts form a grid, never a triangulated fan.
public static class EdgeSubdivision
{
    public static MeshPlans.Rebuild Split(IReadOnlyList<Vector3> pos, IReadOnlyList<int[]> faces,
        IEnumerable<(int A,int B)> selected, int sections, ISet<int>? faceScope = null)
    {
        if (sections < 2 || sections > 16) return MeshPlans.Rebuild.Fail("use 2 to 16 sections");
        if (pos.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) ||
            faces.Any(f => f.Length is not (3 or 4) || f.Distinct().Count() != f.Length || f.Any(v => v < 0 || v >= pos.Count)))
            return MeshPlans.Rebuild.Fail("the mesh contains invalid points or faces");
        if (faceScope != null && (faceScope.Count == 0 || faceScope.Any(f => f < 0 || f >= faces.Count)))
            return MeshPlans.Rebuild.Fail("select faces first");
        var uses = new Dictionary<(int,int),List<int>>();
        for (int f=0;f<faces.Count;f++)
            for (int k=0;k<faces[f].Length;k++)
            {
                var key=FaceMerge.Key(faces[f][k],faces[f][(k+1)%faces[f].Length]);
                if (!uses.TryGetValue(key,out var owners)) uses[key]=owners=new();
                owners.Add(f);
            }
        var edges=selected.Select(e=>FaceMerge.Key(e.A,e.B)).ToHashSet();
        if (edges.Count==0) return MeshPlans.Rebuild.Fail("select edges first");
        if (edges.Any(e=>!uses.ContainsKey(e))) return MeshPlans.Rebuild.Fail("select edges belonging to a face");
        if (faceScope != null && edges.Any(e => !uses[e].Any(faceScope.Contains)))
            return MeshPlans.Rebuild.Fail("select edges of the selected faces");
        var pending=new Queue<(int,int)>(edges);
        while (pending.TryDequeue(out var edge))
        {
            if (uses[edge].Count>2) return MeshPlans.Rebuild.Fail("a selected cut reaches an edge shared by more than two faces");
            foreach (int f in uses[edge])
            {
                if (faceScope != null && !faceScope.Contains(f)) continue;
                var c=faces[f];
                if (c.Length is not (3 or 4)) return MeshPlans.Rebuild.Fail("straight splits need triangle or quad faces");
                int k=Enumerable.Range(0,c.Length).First(i=>FaceMerge.Key(c[i],c[(i+1)%c.Length])==edge);
                // Triangles receive an even triangular grid so a cut never ends in a fan at their apex.
                foreach (int side in c.Length==4?new[]{(k+2)%4}:Enumerable.Range(0,3))
                {
                    var next=FaceMerge.Key(c[side],c[(side+1)%c.Length]);
                    if(edges.Add(next)) pending.Enqueue(next);
                }
            }
        }
        var points=new List<MeshPlans.NewPoint>();
        var cuts=new Dictionary<(int,int),int[]>();
        int Add(Vector3 p,(int V,float W)[] blend)
        { int id=pos.Count+points.Count; points.Add(new(p,blend)); return id; }
        foreach(var (a,b) in edges.OrderBy(e=>e.Item1).ThenBy(e=>e.Item2))
        {
            if(Vector3.DistanceSquared(pos[a],pos[b])<1e-12f) return MeshPlans.Rebuild.Fail("an edge is too short to split");
            var path=new int[sections+1]; path[0]=a; path[^1]=b;
            for(int i=1;i<sections;i++)
            {
                float t=i/(float)sections;
                path[i]=Add(Vector3.Lerp(pos[a],pos[b],t),new[]{(a,1-t),(b,t)});
            }
            cuts[(a,b)]=path;
        }
        int OnEdge(int a,int b,int i,int count)
        {
            if(i==0) return a;
            if(i==count) return b;
            var key=FaceMerge.Key(a,b);
            return cuts[key][a==key.Item1?i:sections-i];
        }
        var remove=new List<int>(); var add=new List<MeshPlans.NewFace>();
        for(int f=0;f<faces.Count;f++)
        {
            var c=faces[f];
            bool Cut(int k)=>cuts.ContainsKey(FaceMerge.Key(c[k],c[(k+1)%c.Length]));
            if(!Enumerable.Range(0,c.Length).Any(Cut)) continue;
            remove.Add(f);
            if (faceScope != null && !faceScope.Contains(f))
            {
                // Stop the strip at this neighbour, but retain every shared boundary vertex.
                // Sprocket cannot save/clone an n-gon, so fill the expanded boundary with
                // triangles and quads rather than leaving it as one unsupported face.
                var boundary = new List<int>();
                // Exact edge parameters avoid tiny sliver triangles caused by float rounding
                // when a rotated/transformed plate's subdivided border is projected again.
                var uvCorners = c.Length == 4
                    ? new[] { Vector3.Zero, Vector3.UnitX, new Vector3(1, 1, 0), Vector3.UnitY }
                    : new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
                var uv = new List<Vector3>();
                for (int k=0;k<c.Length;k++)
                {
                    int a=c[k], b=c[(k+1)%c.Length]; boundary.Add(a);
                    uv.Add(uvCorners[k]);
                    if (Cut(k)) for(int s=1;s<sections;s++)
                    {
                        boundary.Add(OnEdge(a,b,s,sections));
                        uv.Add(Vector3.Lerp(uvCorners[k], uvCorners[(k+1)%c.Length], s/(float)sections));
                    }
                }
                var normal = HoleRing.Normal(c.Select(v => pos[v]).ToList());
                bool convex = Enumerable.Range(0, c.Length).All(k =>
                    Vector3.Dot(Vector3.Cross(pos[c[(k+1)%c.Length]] - pos[c[k]],
                        pos[c[(k+2)%c.Length]] - pos[c[(k+1)%c.Length]]), normal) >= -1e-6f * normal.LengthSquared());
                // A concave quad cannot be treated as a square: its diagonal must stay
                // inside the authored outline. Tessellate its actual boundary instead.
                var layout = convex ? uv : boundary.Select(v => v < pos.Count ? pos[v] : points[v-pos.Count].P).ToList();
                var filled = Fill.Region(layout, Enumerable.Range(0, boundary.Count).ToList(), new(), convex ? Vector3.UnitZ : normal, null);
                if (filled.Count == 0 || filled.Any(face => face.Length is not (3 or 4)))
                    return MeshPlans.Rebuild.Fail("a face beside the split couldn't be rebuilt as triangles or quads");
                add.AddRange(filled.Select(face => new MeshPlans.NewFace(face.Select(v => boundary[v]).ToArray(), f)));
                continue;
            }
            if(c.Length==4)
            {
                int nu=Cut(0)?sections:1, nv=Cut(1)?sections:1;
                var grid=new int[nu+1,nv+1];
                for(int j=0;j<=nv;j++) for(int i=0;i<=nu;i++)
                {
                    if(j==0) grid[i,j]=OnEdge(c[0],c[1],i,nu);
                    else if(j==nv) grid[i,j]=OnEdge(c[3],c[2],i,nu);
                    else if(i==0) grid[i,j]=OnEdge(c[0],c[3],j,nv);
                    else if(i==nu) grid[i,j]=OnEdge(c[1],c[2],j,nv);
                    else
                    {
                        float u=i/(float)nu,v=j/(float)nv;
                        float a=(1-u)*(1-v),b=u*(1-v),d=(1-u)*v,e=u*v;
                        grid[i,j]=Add(a*pos[c[0]]+b*pos[c[1]]+e*pos[c[2]]+d*pos[c[3]],
                            new[]{(c[0],a),(c[1],b),(c[2],e),(c[3],d)});
                    }
                }
                for(int j=0;j<nv;j++) for(int i=0;i<nu;i++)
                    add.Add(new(new[]{grid[i,j],grid[i+1,j],grid[i+1,j+1],grid[i,j+1]},f));
            }
            else
            {
                int n=sections; var grid=new int[n+1,n+1];
                for(int j=0;j<=n;j++) for(int i=0;i+j<=n;i++)
                {
                    if(j==0) grid[i,j]=OnEdge(c[0],c[1],i,n);
                    else if(i==0) grid[i,j]=OnEdge(c[0],c[2],j,n);
                    else if(i+j==n) grid[i,j]=OnEdge(c[1],c[2],j,n);
                    else
                    {
                        float u=i/(float)n,v=j/(float)n;
                        grid[i,j]=Add((1-u-v)*pos[c[0]]+u*pos[c[1]]+v*pos[c[2]],new[]{(c[0],1-u-v),(c[1],u),(c[2],v)});
                    }
                }
                for(int j=0;j<n;j++) for(int i=0;i+j<n;i++)
                {
                    add.Add(new(new[]{grid[i,j],grid[i+1,j],grid[i,j+1]},f));
                    if(i+j<n-1) add.Add(new(new[]{grid[i+1,j],grid[i+1,j+1],grid[i,j+1]},f));
                }
            }
        }
        return new(remove,add,points,null);
    }
}
