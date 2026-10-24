using System.Numerics;
using System.Text.Json.Nodes;

namespace SprocketQoL;

/// Shape-only thumbnails from saved geometry. No prefab loading or tank instantiation.
public static class WorkshopGeometry
{
    public static ObjDocument Read(string json)
    {
        var design = Conversion.Parse(json); var objects = Conversion.Objects(design);
        if (design["rootTransforms"] is JsonObject roots)
            foreach (var entry in roots)
                if (int.TryParse(entry.Key,out int id) && objects.TryGetValue(id,out var part) && entry.Value is JsonArray a && a.Count==16)
                {
                    float[] v=a.Select(MeshCut.F).ToArray();
                    var m=new Matrix4x4(v[0],v[1],v[2],v[3],v[4],v[5],v[6],v[7],v[8],v[9],v[10],v[11],v[12],v[13],v[14],v[15]);
                    part["pvuid"]=-1; Conversion.WriteTransform(part["transform"]!.AsObject(),m);
                }
        var world=Conversion.WorldMatrices(objects);
        var blocks=(design["blueprints"] as JsonArray ?? new()).ToDictionary(b=>Conversion.Id(b!,"id"));
        var meshes=(design["meshes"] as JsonArray ?? new()).ToDictionary(m=>Conversion.Id(m!,"vuid"));
        var result=new List<ObjMeshData>(); int budget=200_000;
        foreach (var (id,part) in objects)
        {
            int blockId=part["structureBlueprintVuid"]?.GetValue<int>() ?? -1;
            if (!blocks.TryGetValue(blockId,out var block)) continue;
            int meshId=block?["blueprint"]?["bodyMeshVuid"]?.GetValue<int>() ?? -1;
            if (!meshes.TryGetValue(meshId,out var mesh) || mesh?["meshData"]?["mesh"] is not JsonObject shape) continue;
            var raw=shape["vertices"]!.AsArray();
            if (raw.Count%3!=0 || raw.Count/3>budget) continue;
            budget-=raw.Count/3;
            var transform=AddonEdits.Shape(part,world[id]);
            var positions=Enumerable.Range(0,raw.Count/3).Select(i=>Vector3.Transform(new Vector3(MeshCut.F(raw[i*3]),MeshCut.F(raw[i*3+1]),MeshCut.F(raw[i*3+2])),transform)).ToArray();
            foreach(ref var p in positions.AsSpan()) p.X=-p.X; // GeometryPreview consumes OBJ coordinates and reflects X back.
            var faces=shape["faces"]!.AsArray().Select(f=>f!["v"]!.AsArray().Select(n=>n!.GetValue<int>()).Reverse().ToArray()).ToArray();
            if (faces.Any(f=>f.Any(i=>i<0||i>=positions.Length))) throw new FormatException("Invalid thumbnail face indices.");
            result.Add(new("Structure "+id,positions,faces));
        }
        return new(result.ToArray());
    }
}
