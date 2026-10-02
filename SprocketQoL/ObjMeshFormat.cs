using System.Globalization;
using System.Numerics;
using System.Text;

namespace SprocketQoL;

public readonly record struct ObjCorner(int Vertex, int TexCoord = -1, int Normal = -1);
public record ObjMeshData(string Name, Vector3[] Vertices, int[][] Faces,
    Vector2[]? TexCoords = null, Vector3[]? Normals = null, ObjCorner[][]? FaceCorners = null,
    string[]? FaceMaterials = null);
public record ObjDocument(ObjMeshData[] Meshes);

/// Wavefront polygon data in right-handed, Y-up coordinates. No paths are opened by this codec.
public static class ObjMeshFormat
{
    public const int MaxTextLength = 64 * 1024 * 1024;
    public const int MaxLineLength = 256 * 1024;
    public const int MaxVertices = 1_000_000;
    public const int MaxFaces = 1_000_000;
    public const int MaxCorners = 4_000_000;
    public const int MaxFaceVertices = 256;
    public const int MaxMeshes = 4096;
    public const float MaxCoordinate = 1_000_000;
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static ObjDocument Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextLength) throw new FormatException("The OBJ is too large (maximum 64 MiB of text).");
        var vertices = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var normals = new List<Vector3>();
        var meshes = new List<ObjMeshData>();
        var faces = new List<ObjCorner[]>();
        var materials = new List<string>();
        string objectName = "", groupName = "", material = "";
        int lineNumber = 0, totalFaces = 0, totalCorners = 0;
        int localVertexTotal = 0, localUvTotal = 0, localNormalTotal = 0;

        FormatException Bad(string message) => new($"OBJ line {lineNumber}: {message}");
        float Number(string token)
        {
            if (!float.TryParse(token, NumberStyles.Float, Invariant, out float value) ||
                !float.IsFinite(value) || Math.Abs(value) > MaxCoordinate)
                throw Bad("coordinates must be finite numbers within +/- 1,000,000.");
            return value;
        }
        int Index(string token, int count, string kind)
        {
            if (!int.TryParse(token, NumberStyles.AllowLeadingSign, Invariant, out int value) || value == 0)
                throw Bad($"invalid {kind} index '{token}'.");
            long absolute = value > 0 ? (long)value - 1 : (long)count + value;
            if (absolute < 0 || absolute >= count)
                throw Bad($"{kind} index '{token}' is outside the data declared before this face.");
            return (int)absolute;
        }
        void Flush()
        {
            if (faces.Count == 0) return;
            if (meshes.Count >= MaxMeshes) throw Bad("there are too many objects or groups.");
            var vertexMap = new Dictionary<int, int>();
            var uvMap = new Dictionary<int, int>();
            var normalMap = new Dictionary<int, int>();
            var localVertices = new List<Vector3>();
            var localUv = new List<Vector2>();
            var localNormals = new List<Vector3>();
            int Local<T>(int global, Dictionary<int, int> map, List<T> source, List<T> target)
            {
                if (global < 0) return -1;
                if (!map.TryGetValue(global, out int local))
                { local = target.Count; map.Add(global, local); target.Add(source[global]); }
                return local;
            }
            var localFaces = new int[faces.Count][];
            var localCorners = new ObjCorner[faces.Count][];
            for (int f = 0; f < faces.Count; f++)
            {
                localFaces[f] = new int[faces[f].Length];
                localCorners[f] = new ObjCorner[faces[f].Length];
                for (int c = 0; c < faces[f].Length; c++)
                {
                    var corner = faces[f][c];
                    int v = Local(corner.Vertex, vertexMap, vertices, localVertices);
                    localFaces[f][c] = v;
                    localCorners[f][c] = new(v, Local(corner.TexCoord, uvMap, texCoords, localUv),
                        Local(corner.Normal, normalMap, normals, localNormals));
                }
            }
            localVertexTotal += localVertices.Count; localUvTotal += localUv.Count; localNormalTotal += localNormals.Count;
            if (localVertexTotal > MaxVertices || localUvTotal > MaxVertices || localNormalTotal > MaxVertices)
                throw Bad("separate objects and groups contain too many referenced vertices or attributes.");
            string name = objectName;
            if (groupName.Length != 0) name = name.Length == 0 ? groupName : name + " / " + groupName;
            if (name.Length == 0) name = "Mesh " + (meshes.Count + 1).ToString(Invariant);
            meshes.Add(new(name, localVertices.ToArray(), localFaces,
                localUv.Count == 0 ? null : localUv.ToArray(), localNormals.Count == 0 ? null : localNormals.ToArray(),
                localCorners, materials.ToArray()));
            faces.Clear(); materials.Clear();
        }

        using var reader = new StringReader(text);
        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (raw.Length > MaxLineLength) throw Bad("the line is too long.");
            if (lineNumber == 1) raw = raw.TrimStart('\uFEFF');
            int comment = raw.IndexOf('#');
            string line = (comment >= 0 ? raw[..comment] : raw).Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string Value()
            {
                string value = line[parts[0].Length..].Trim();
                if (value.Length > 4096) throw Bad("object, group and material names must be at most 4096 characters.");
                return value;
            }
            switch (parts[0])
            {
                case "v":
                    // Some exporters append a homogeneous weight or vertex colours. Neither changes polygon positions.
                    if (parts.Length < 4 || parts.Length > 8) throw Bad("a vertex needs x, y and z coordinates.");
                    if (vertices.Count >= MaxVertices) throw Bad("there are too many vertices.");
                    var v = new Vector3(Number(parts[1]), Number(parts[2]), Number(parts[3]));
                    for (int i = 4; i < parts.Length; i++) Number(parts[i]);
                    vertices.Add(v);
                    break;
                case "vt":
                    if (parts.Length < 2 || parts.Length > 4) throw Bad("a texture coordinate needs one to three numbers.");
                    if (texCoords.Count >= MaxVertices) throw Bad("there are too many texture coordinates.");
                    texCoords.Add(new(Number(parts[1]), parts.Length >= 3 ? Number(parts[2]) : 0));
                    if (parts.Length == 4) Number(parts[3]);
                    break;
                case "vn":
                    if (parts.Length != 4) throw Bad("a normal needs three numbers.");
                    if (normals.Count >= MaxVertices) throw Bad("there are too many normals.");
                    normals.Add(new(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "f":
                    if (parts.Length < 4 || parts.Length > MaxFaceVertices + 1)
                        throw Bad($"a face needs 3 to {MaxFaceVertices} corners.");
                    if (++totalFaces > MaxFaces || (totalCorners += parts.Length - 1) > MaxCorners)
                        throw Bad("there are too many faces or face corners.");
                    var corners = new ObjCorner[parts.Length - 1];
                    var seen = new HashSet<int>();
                    for (int c = 0; c < corners.Length; c++)
                    {
                        var ids = parts[c + 1].Split('/');
                        if (ids.Length > 3 || ids[0].Length == 0 ||
                            (ids.Length == 2 && ids[1].Length == 0) ||
                            (ids.Length == 3 && ids[2].Length == 0))
                            throw Bad($"invalid face corner '{parts[c + 1]}'.");
                        int vi = Index(ids[0], vertices.Count, "vertex");
                        if (!seen.Add(vi)) throw Bad("a face repeats a vertex.");
                        int ti = ids.Length >= 2 && ids[1].Length != 0 ? Index(ids[1], texCoords.Count, "texture") : -1;
                        int ni = ids.Length == 3 ? Index(ids[2], normals.Count, "normal") : -1;
                        corners[c] = new(vi, ti, ni);
                    }
                    faces.Add(corners); materials.Add(material);
                    break;
                case "o":
                    Flush(); objectName = Value(); groupName = "";
                    break;
                case "g":
                    Flush(); groupName = Value();
                    break;
                case "usemtl":
                    material = Value();
                    if (material.Length == 0) throw Bad("usemtl needs a material name.");
                    if (material == "off") material = "";
                    break;
                // mtllib is deliberately only a reference; smoothing, lines and curves do not define polygon faces.
            }
        }
        Flush();
        if (meshes.Count == 0) throw new FormatException("The OBJ contains no polygon faces to import.");
        return new(meshes.ToArray());
    }

    public static string Write(ObjDocument document, string? materialLibrary = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Meshes == null || document.Meshes.Length == 0 || document.Meshes.Length > MaxMeshes)
            throw new ArgumentException("Export needs 1 to 4096 meshes.", nameof(document));
        if (materialLibrary != null && (materialLibrary.IndexOfAny(new[] { '\r', '\n', '\0', '#' }) >= 0 ||
            materialLibrary.Length > 4096))
            throw new ArgumentException("The material library reference must fit on one OBJ line.", nameof(materialLibrary));

        var output = new StringBuilder("# Sprocket polygon export; right-handed Y-up; coordinates in metres\n");
        if (!string.IsNullOrWhiteSpace(materialLibrary)) output.Append("mtllib ").Append(materialLibrary.Trim()).Append('\n');
        int vertexOffset = 1, uvOffset = 1, normalOffset = 1, totalFaces = 0, totalCorners = 0;
        string activeMaterial = "";
        string Number(float value)
        {
            if (!float.IsFinite(value) || Math.Abs(value) > MaxCoordinate)
                throw new ArgumentException("Mesh coordinates must be finite numbers within +/- 1,000,000.", nameof(document));
            return value.ToString("R", Invariant);
        }
        string Label(string? value, string fallback)
        {
            string label = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Replace('\0', ' ').Replace('#', '_').Trim();
            if (label.Length > 4096) throw new ArgumentException("Mesh and material names must be at most 4096 characters.", nameof(document));
            return label.Length == 0 ? fallback : label;
        }
        void WithinTextLimit()
        {
            if (output.Length > MaxTextLength) throw new ArgumentException("Exported OBJ exceeds the 64 MiB text limit.", nameof(document));
        }
        for (int m = 0; m < document.Meshes.Length; m++)
        {
            var mesh = document.Meshes[m];
            if (mesh == null || mesh.Vertices == null || mesh.Faces == null || mesh.Vertices.Length == 0 || mesh.Faces.Length == 0)
                throw new ArgumentException("Every exported mesh needs vertices and polygon faces.", nameof(document));
            int uvCount = mesh.TexCoords?.Length ?? 0, normalCount = mesh.Normals?.Length ?? 0;
            if ((long)vertexOffset + mesh.Vertices.Length - 1 > MaxVertices ||
                (long)uvOffset + uvCount - 1 > MaxVertices || (long)normalOffset + normalCount - 1 > MaxVertices)
                throw new ArgumentException("Export contains too many vertices, texture coordinates or normals.", nameof(document));
            if (mesh.FaceCorners != null && mesh.FaceCorners.Length != mesh.Faces.Length ||
                mesh.FaceMaterials != null && mesh.FaceMaterials.Length != mesh.Faces.Length)
                throw new ArgumentException("Face attributes must match the number of faces.", nameof(document));

            output.Append('\n').Append("o ").Append(Label(mesh.Name, "Mesh " + (m + 1).ToString(Invariant))).Append('\n');
            foreach (var p in mesh.Vertices)
            {
                output.Append("v ").Append(Number(p.X)).Append(' ').Append(Number(p.Y)).Append(' ').Append(Number(p.Z)).Append('\n');
                WithinTextLimit();
            }
            if (mesh.TexCoords != null) foreach (var uv in mesh.TexCoords)
            {
                output.Append("vt ").Append(Number(uv.X)).Append(' ').Append(Number(uv.Y)).Append('\n');
                WithinTextLimit();
            }
            if (mesh.Normals != null) foreach (var n in mesh.Normals)
            {
                output.Append("vn ").Append(Number(n.X)).Append(' ').Append(Number(n.Y)).Append(' ').Append(Number(n.Z)).Append('\n');
                WithinTextLimit();
            }
            for (int f = 0; f < mesh.Faces.Length; f++)
            {
                var face = mesh.Faces[f];
                if (face == null || face.Length < 3 || face.Length > MaxFaceVertices || face.Distinct().Count() != face.Length ||
                    face.Any(v => v < 0 || v >= mesh.Vertices.Length))
                    throw new ArgumentException("Faces need 3 to 256 distinct, valid vertex indices.", nameof(document));
                if (++totalFaces > MaxFaces || (totalCorners += face.Length) > MaxCorners)
                    throw new ArgumentException("Export contains too many faces or face corners.", nameof(document));
                var corners = mesh.FaceCorners?[f];
                if (mesh.FaceCorners != null && (corners == null || corners.Length != face.Length))
                    throw new ArgumentException("Face corner attributes must match each polygon.", nameof(document));
                string material = Label(mesh.FaceMaterials?[f], "");
                if (material != activeMaterial)
                {
                    output.Append("usemtl ").Append(material.Length == 0 ? "off" : material).Append('\n');
                    activeMaterial = material;
                }
                output.Append('f');
                for (int c = 0; c < face.Length; c++)
                {
                    var corner = corners == null ? new ObjCorner(face[c]) : corners[c];
                    if (corner.Vertex != face[c] || corner.TexCoord < -1 || corner.TexCoord >= uvCount ||
                        corner.Normal < -1 || corner.Normal >= normalCount)
                        throw new ArgumentException("A face corner refers to missing or mismatched mesh data.", nameof(document));
                    output.Append(' ').Append((corner.Vertex + vertexOffset).ToString(Invariant));
                    if (corner.TexCoord >= 0 || corner.Normal >= 0)
                    {
                        output.Append('/');
                        if (corner.TexCoord >= 0) output.Append((corner.TexCoord + uvOffset).ToString(Invariant));
                        if (corner.Normal >= 0) output.Append('/').Append((corner.Normal + normalOffset).ToString(Invariant));
                    }
                }
                output.Append('\n'); WithinTextLimit();
            }
            vertexOffset += mesh.Vertices.Length; uvOffset += uvCount; normalOffset += normalCount;
        }
        return output.ToString();
    }
}
