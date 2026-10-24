using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Numerics;

namespace SprocketQoL;

/// Named selections are bound to one loaded design, one part and an exact topology.
/// Coordinates are deliberately excluded: moving vertices should not invalidate a set.
public sealed class SelectionMemory
{
    public readonly record struct Scope(string Design, long Part, string Topology);
    public sealed record Saved(string Name, Scope Scope, int Mode, int[] Points, int[] Edges, int[] Faces);
    readonly Dictionary<(string Design, long Part, string Name), Saved> sets = new();
    public const int MaximumPerPart = 32;

    public static string Fingerprint(int vertexCount, IReadOnlyList<(int A, int B)> edges,
        IReadOnlyList<int[]> faces, IReadOnlyList<long>? vertexIdentity = null)
    {
        if (vertexCount < 0 || edges == null || faces == null
            || vertexIdentity != null && vertexIdentity.Count != vertexCount)
            throw new ArgumentException("Invalid mesh topology.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(long value) => hash.AppendData(BitConverter.GetBytes(value));
        Add(vertexCount); Add(edges.Count); Add(faces.Count);
        if (vertexIdentity != null) foreach (long id in vertexIdentity) Add(id);
        foreach (var (a, b) in edges)
        {
            if (a < 0 || b < 0 || a >= vertexCount || b >= vertexCount) throw new ArgumentException("Invalid mesh edge.");
            Add(a); Add(b);
        }
        foreach (var face in faces)
        {
            if (face == null || face.Length < 3 || face.Any(v => v < 0 || v >= vertexCount)) throw new ArgumentException("Invalid mesh face.");
            Add(face.Length); foreach (int point in face) Add(point);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public bool Save(string name, Scope scope, int mode, IEnumerable<int> points, IEnumerable<int> edges,
        IEnumerable<int> faces, out string message)
    {
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) { message = "Use a selection name from 1 to 60 characters."; return false; }
        if (string.IsNullOrEmpty(scope.Design) || string.IsNullOrEmpty(scope.Topology)) { message = "No loaded design or topology."; return false; }
        var p = points.Distinct().OrderBy(v => v).ToArray();
        var e = edges.Distinct().OrderBy(v => v).ToArray();
        var f = faces.Distinct().OrderBy(v => v).ToArray();
        if (p.Length + e.Length + f.Length == 0 || p.Concat(e).Concat(f).Any(v => v < 0)) { message = "Select some geometry first."; return false; }
        if((long)p.Length+e.Length+f.Length>SelectionSetStore.MaximumSelectedElements||p.Concat(e).Concat(f).Any(v=>v>=SelectionSetStore.MaximumVertices))
        {message="A selection set is limited to 65,535 elements.";return false;}
        var key = (scope.Design, scope.Part, name);
        if (!sets.ContainsKey(key) && sets.Values.Count(s => s.Scope.Design == scope.Design && s.Scope.Part == scope.Part) >= MaximumPerPart)
        { message = $"This part already has {MaximumPerPart} named selections."; return false; }
        sets[key] = new(name, scope, mode, p, e, f);
        message = $"Saved selection '{name}'."; return true;
    }

    public Saved? Recall(string name, Scope scope, int vertexCount, int edgeCount, int faceCount, out string message)
    {
        if (!sets.TryGetValue((scope.Design, scope.Part, name ?? ""), out var saved))
        { message = "That selection does not belong to this loaded design and part."; return null; }
        if (vertexCount<0||edgeCount<0||faceCount<0||saved.Scope.Topology != scope.Topology || saved.Points.Any(v => v >= vertexCount)
            || saved.Edges.Any(v => v >= edgeCount) || saved.Faces.Any(v => v >= faceCount))
        { message = "The mesh topology changed. Undo that change or save this selection again."; return null; }
        // Return copies: a caller cannot silently corrupt stored indices.
        message = $"Recalled selection '{saved.Name}'.";
        return saved with { Points = saved.Points.ToArray(), Edges = saved.Edges.ToArray(), Faces = saved.Faces.ToArray() };
    }
    public string[] Names(string design, long part) => sets.Values.Where(s => s.Scope.Design == design && s.Scope.Part == part)
        .Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    public bool Delete(string name, string design, long part) => sets.Remove((design, part, name));
    public void Clear() => sets.Clear();

    /// Restart binding is stricter than an in-session topology guard: every stored
    /// vertex coordinate and ordered face/edge index must be identical.
    public static string GeometryFingerprint(IReadOnlyList<Vector3> positions,IReadOnlyList<(int A,int B)> edges,IReadOnlyList<int[]> faces)
    {
        if(positions.Count>SelectionSetStore.MaximumVertices || positions.Any(p=>!float.IsFinite(p.X)||!float.IsFinite(p.Y)||!float.IsFinite(p.Z)))
            throw new ArgumentException("The selection mesh has too many vertices or invalid positions.");
        string topology=Fingerprint(positions.Count,edges,faces);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(topology));
        foreach(var p in positions)
        {
            hash.AppendData(BitConverter.GetBytes(p.X));hash.AppendData(BitConverter.GetBytes(p.Y));hash.AppendData(BitConverter.GetBytes(p.Z));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

/// Bounded, atomic JSON storage. The user's names never become file-system paths.
/// A damaged or newer-version library remains read-only, preserving it for recovery.
public sealed class SelectionSetStore
{
    public sealed record Item(string Name,string Design,long Part,string Geometry,int Mode,int[] Points,int[] Edges,int[] Faces);
    sealed record Document(int Version,Item[] Items);
    public const int MaximumSets=256,MaximumVertices=65535,MaximumSelectedElements=65535;
    public const long MaximumBytes=8*1024*1024;
    readonly string file;
    readonly Dictionary<(string Design,long Part,string Name),Item> items=new();
    bool readOnly;
    public string LoadMessage {get;private set;}="";
    public SelectionSetStore(string file)
    {
        this.file=Path.GetFullPath(file);
        if(!File.Exists(this.file))return;
        try
        {
            if(new FileInfo(this.file).Length>MaximumBytes)throw new FormatException("Selection library exceeds 8 MiB.");
            var document=JsonSerializer.Deserialize<Document>(File.ReadAllText(this.file));
            if(document?.Version!=1 || document.Items==null || document.Items.Length>MaximumSets)throw new FormatException("Unsupported selection library version or size.");
            foreach(var item in document.Items)
            {
                if(!Valid(item))throw new FormatException("The selection library contains invalid entries.");
                var key=(item.Design,item.Part,item.Name);
                if(items.ContainsKey(key))throw new FormatException("The selection library contains duplicate entries.");
                items[key]=Copy(item);
            }
            if(items.Values.GroupBy(i=>(i.Design,i.Part)).Any(g=>g.Count()>SelectionMemory.MaximumPerPart))
                throw new FormatException("The selection library exceeds its per-part set limit.");
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        {items.Clear();readOnly=true;LoadMessage="Selection library was preserved without changes: "+ex.Message;}
    }
    public static string DesignKey(string factionDirectory,string designName)
    {
        if(string.IsNullOrWhiteSpace(factionDirectory)||string.IsNullOrWhiteSpace(designName))return "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(factionDirectory.Trim().Replace('/','\\').ToUpperInvariant()+"\n"+designName.Trim())));
    }
    public Item[] ForPart(string design,long part)=>items.Values.Where(i=>i.Design==design&&i.Part==part)
        .OrderBy(i=>i.Name,StringComparer.Ordinal).Select(Copy).ToArray();
    public string[] Names(string design,long part)=>items.Values.Where(i=>i.Design==design&&i.Part==part).Select(i=>i.Name).OrderBy(n=>n,StringComparer.Ordinal).ToArray();
    public bool Contains(string name,string design,long part)=>items.ContainsKey((design,part,name));
    public bool Save(Item item,out string message)
    {
        if(!Valid(item)){message="Selection data exceeds its name, count or geometry limits.";return false;}
        if(readOnly){message="Selection saved for this session only; its existing disk library needs recovery.";return false;}
        var key=(item.Design,item.Part,item.Name);
        if(!items.ContainsKey(key)&&(items.Count>=MaximumSets||items.Values.Count(i=>i.Design==item.Design&&i.Part==item.Part)>=SelectionMemory.MaximumPerPart))
        {message="Selection library is full. Delete an old named set first.";return false;}
        items.TryGetValue(key,out var previous);items[key]=Copy(item);
        try{Write();message=$"Saved selection '{item.Name}' to the library.";return true;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if(previous==null)items.Remove(key);else items[key]=previous;
            message="Selection saved for this session only: "+ex.Message;return false;
        }
    }
    public bool Delete(string name,string design,long part,out string message)
    {
        message="";var key=(design,part,name);
        if(readOnly){message="The existing selection library is read-only until recovered.";return false;}
        if(!items.Remove(key,out var previous))return false;
        try{Write();message=$"Deleted selection '{name}'.";return true;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException)
        {items[key]=previous;message="Could not update the selection library: "+ex.Message;return false;}
    }
    void Write()
    {
        string json=JsonSerializer.Serialize(new Document(1,items.Values.OrderBy(i=>i.Design,StringComparer.Ordinal).ThenBy(i=>i.Part).ThenBy(i=>i.Name,StringComparer.Ordinal).ToArray()));
        if(Encoding.UTF8.GetByteCount(json)>MaximumBytes)throw new IOException("Selection library exceeds 8 MiB. Delete an old set first.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temporary,json,new UTF8Encoding(false));File.Move(temporary,file,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    static bool Valid(Item? item)=>item!=null&&item.Name!=null&&item.Name.Length is >=1 and <=60&&!item.Name.Any(char.IsControl)
        &&item.Design!=null&&item.Design.Length==64&&item.Design.All(Uri.IsHexDigit)
        &&item.Geometry!=null&&item.Geometry.Length==64&&item.Geometry.All(Uri.IsHexDigit)&&item.Mode>=0&&item.Mode<=255
        &&item.Part>=0&&item.Part<=uint.MaxValue
        &&item.Points!=null&&item.Edges!=null&&item.Faces!=null&&(long)item.Points.Length+item.Edges.Length+item.Faces.Length is >0 and <=MaximumSelectedElements
        &&item.Points.Concat(item.Edges).Concat(item.Faces).All(i=>i>=0&&i<MaximumVertices);
    static Item Copy(Item item)=>item with{Points=item.Points.ToArray(),Edges=item.Edges.ToArray(),Faces=item.Faces.ToArray()};
}

/// Deliberate target cycling remains stable across repeated native samples in one frame.
public sealed class CandidateCycle
{
    string scope = "";
    System.Numerics.Vector2 origin;
    int? chosen;
    int lastAdvance = -1;
    public int Choose(string context, System.Numerics.Vector2 pointer, IReadOnlyList<int> ids, bool advance, int frame)
    {
        if (context != scope || System.Numerics.Vector2.DistanceSquared(pointer, origin) > 16)
        { scope = context; origin = pointer; chosen = null; }
        if (ids.Count == 0) { chosen = null; return -1; }
        int index = chosen is int selected ? IndexOf(ids, selected) : 0;
        if (index < 0) index = 0;
        if (advance && lastAdvance != frame) { index = (index + 1) % ids.Count; lastAdvance = frame; }
        chosen = ids[index]; return index;
    }
    public void Clear() { scope = ""; chosen = null; lastAdvance = -1; }
    static int IndexOf(IReadOnlyList<int> ids, int value) { for (int i = 0; i < ids.Count; i++) if (ids[i] == value) return i; return -1; }
}
