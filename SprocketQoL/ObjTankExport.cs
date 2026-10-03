using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Sprocket.Vehicles;
using Sprocket.Vehicles.AssetManagement;
using Sprocket.Vehicles.AttachedBehaviours;
using Sprocket.Vehicles.PlateStructures;
using Sprocket.Vehicles.Tracks.Belts;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using N = System.Numerics;

namespace SprocketQoL;

internal record ObjExportPart(int Id, string Name, ObjExportCategory Category, VehicleObject Part);

/// Reads native render geometry without moving, rebuilding or changing any vehicle part.
internal static class ObjTankExport
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    internal static bool DefaultIncluded(ObjExportCategory category) => ObjExportPolicy.DefaultIncluded(category);

    internal static List<ObjExportPart> CollectParts(DesignEditor editor)
    {
        var parts = new List<ObjExportPart>();
        var seen = new HashSet<int>();
        foreach (var part in editor.AllParts())
        {
            int id = (int)part.VUID;
            if (!seen.Add(id)) continue;
            var category = Category(part);
            string name = ObjExportPolicy.KnownParts.TryGetValue(part.GUID ?? "", out var known) ? known.Name : part.name;
            var structure = DesignEditor.Each(part.Components).Select(c => c?.TryCast<PlateStructure>()).FirstOrDefault(s => s != null);
            if (!string.IsNullOrWhiteSpace(structure?.Blueprint?.Name)) name = structure.Blueprint.Name;
            if (part.IsVehicleRoot) name = "Main hull";
            if (string.IsNullOrWhiteSpace(name)) name = "Part";
            parts.Add(new(id, name, category, part));
        }
        return parts;
    }

    static ObjExportCategory Category(VehicleObject part)
    {
        // Component IDs come from the part registry, not the user's part name or translated labels.
        return ObjExportPolicy.Classify(part.GUID, DesignEditor.Each(part.Components).Where(c => c != null).Select(c => c.ComponentTypeID), part.HasTag("internal"));
    }

    internal static string Export(DesignEditor editor, IReadOnlySet<int> selectedIds, string destinationPath)
    {
        if (editor.Core?.Target == null || !editor.IsReady || editor.IsBusy)
            throw new InvalidOperationException("Open a tank in the editor before exporting.");
        if (ExplodedView.Active) throw new InvalidOperationException($"Turn off exploded view ({Keybinds.Shown("explode")}) before exporting, so all parts keep their normal positions.");
        string destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".obj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a file ending in .obj.");
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The export folder no longer exists.");
        string stem = Path.GetFileNameWithoutExtension(destination);
        if (stem.IndexOfAny(new[] { '\r', '\n', '#', '\0' }) >= 0)
            throw new ArgumentException("The export file name contains characters OBJ cannot store safely.");
        string safeStem = string.Concat(stem.Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? c : '_'));
        if (string.IsNullOrWhiteSpace(safeStem)) safeStem = "tank";
        string mtlName = safeStem + ".mtl", assetName = safeStem + "_assets";
        string mtlPath = Path.Combine(directory, mtlName), assetPath = Path.Combine(directory, assetName);
        if (File.Exists(destination) || File.Exists(mtlPath) || Directory.Exists(assetPath) || File.Exists(assetPath))
            throw new IOException("Choose a new export name. Existing OBJ, material and texture files are kept.");
        var state = new ExportState(editor, selectedIds, assetName);
        state.Read(); // All native reads succeed before a package is published.
        string obj = ObjMeshFormat.Write(new(state.Meshes.ToArray()), mtlName);
        string stage = Path.Combine(directory, ".qol-obj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        bool assetsMoved = false, mtlMoved = false, objMoved = false;
        try
        {
            string stagedAssets = Path.Combine(stage, assetName);
            Directory.CreateDirectory(stagedAssets);
            foreach (var (file, bytes) in state.Textures) File.WriteAllBytes(Path.Combine(stagedAssets, file), bytes);
            File.WriteAllText(Path.Combine(stage, mtlName), state.MaterialText.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(stage, Path.GetFileName(destination)), obj, new UTF8Encoding(false));
            // OBJ is the final commit marker. No existing output is replaced, including a concurrent export.
            Directory.Move(stagedAssets, assetPath); assetsMoved = true;
            File.Move(Path.Combine(stage, mtlName), mtlPath); mtlMoved = true;
            File.Move(Path.Combine(stage, Path.GetFileName(destination)), destination); objMoved = true;
        }
        catch
        {
            if (objMoved) File.Delete(destination);
            if (mtlMoved) File.Delete(mtlPath);
            if (assetsMoved) Directory.Delete(assetPath, true);
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        return $"Exported {state.Meshes.Count} meshes and {state.DecalCount} decals with {state.Textures.Count} textures. All parts keep their position and size.\n{destination}";
    }

    sealed record RawMesh(N.Vector3[] Points, N.Vector3[] Normals, N.Vector2[] UV, int[][] Faces, int[] Submeshes);
    sealed record Receiver(RawMesh Raw, N.Vector3[] World, N.Vector3[] WorldNormals, Renderer Renderer);

    sealed class ExportState
    {
        readonly DesignEditor editor;
        readonly IReadOnlySet<int> selected;
        readonly string assetName;
        readonly Dictionary<int, VehicleObject> parts;
        readonly Dictionary<IntPtr, VehicleObject> modelOwners = new();
        readonly Dictionary<IntPtr, RawMesh> meshCache = new();
        readonly HashSet<IntPtr> seen = new(), ignored = new();
        readonly Dictionary<(IntPtr Material, float Opacity, bool Decal), string> materials = new();
        readonly Dictionary<(IntPtr Texture, bool Alpha), string> textureNames = new();
        readonly List<Receiver> receivers = new();
        readonly N.Matrix4x4 worldToVehicle, normalToVehicle;
        internal readonly List<ObjMeshData> Meshes = new();
        internal readonly StringBuilder MaterialText = new("# Sprocket OBJ materials\n");
        internal readonly Dictionary<string, byte[]> Textures = new();
        internal int DecalCount;
        int vertices, faces;

        internal ExportState(DesignEditor editor, IReadOnlySet<int> selected, string assetName)
        {
            this.editor = editor; this.selected = selected; this.assetName = assetName;
            parts = editor.AllParts().GroupBy(p => (int)p.VUID).ToDictionary(g => g.Key, g => g.First());
            var root = parts.Values.FirstOrDefault(p => p.IsVehicleRoot)?.VehicleTransform ??
                parts.Values.Select(p => p.VehicleTransform?.Root).FirstOrDefault(p => p != null);
            if (root == null) throw new InvalidOperationException("The tank's coordinate frame is unavailable.");
            Vector3 o = root.WorldToVehicleSpace(Vector3.zero);
            worldToVehicle = ObjExportGeometry.FromBasis(V(o), V(root.WorldToVehicleSpace(Vector3.right) - o),
                V(root.WorldToVehicleSpace(Vector3.up) - o), V(root.WorldToVehicleSpace(Vector3.forward) - o));
            normalToVehicle = ObjExportGeometry.NormalMatrix(worldToVehicle);
        }

        internal void Read()
        {
            foreach (var part in parts.Values)
                foreach (var component in DesignEditor.Each(part.Components))
                    if (component?.models is { } models)
                        for (int i = 0; i < models.Count; i++)
                            if (models[i] is { } group) modelOwners[group.Pointer] = part;

            var gateway = editor.Core!.Target.Cast<IVehicleGateway>();
            if (gateway.RendererRegister?.Groups is { } groups)
                foreach (var group in DesignEditor.Each(groups))
                {
                    if (group == null) continue;
                    if (group.shadow?.Renderer is { } shadow) ignored.Add(shadow.Pointer);
                    if (group.lods is not { } lods) continue;
                    VehicleRenderer? chosen = null;
                    for (int i = 0; i < lods.Length; i++)
                        if (lods[i]?.Renderer is { } r)
                        {
                            ignored.Add(r.Pointer); // never export duplicate low LODs or shadow proxies
                            chosen ??= lods[i];
                        }
                    if (chosen?.Renderer is not { } renderer) continue;
                    modelOwners.TryGetValue(group.Pointer, out var owner);
                    if (owner == null) parts.TryGetValue((int)group.id, out owner);
                    owner ??= renderer.GetComponentInParent<VehicleObject>();
                    if (owner == null || !selected.Contains((int)owner.VUID)) continue;
                    AddRenderer(renderer, owner, group.Materials);
                }

            // Renderers created directly by custom parts and track belt segments need no registry group.
            foreach (var part in parts.Values)
            {
                foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
                {
                    if (ignored.Contains(renderer.Pointer) || seen.Contains(renderer.Pointer)) continue;
                    var owner = renderer.GetComponentInParent<VehicleObject>() ?? part;
                    if (selected.Contains((int)owner.VUID)) AddRenderer(renderer, owner, null);
                }
                if (!selected.Contains((int)part.VUID)) continue;
                foreach (var component in DesignEditor.Each(part.Components))
                    if (component?.TryCast<TrackBelt>() is { } belt && belt.segmentModels is { } segments)
                        for (int i = 0; i < segments.Length; i++)
                            if (segments[i]?.renderer is { } renderer && !seen.Contains(renderer.Pointer))
                                AddRenderer(renderer, part, null);
            }
            ReadDecals();
            if (Meshes.Count == 0) throw new InvalidOperationException("The selected parts contain no polygon geometry.");
        }

        void AddRenderer(Renderer renderer, VehicleObject owner, Il2CppReferenceArray<VehicleMaterial>? originals)
        {
            if (!seen.Add(renderer.Pointer)) return;
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) return;
            // Particle systems, UI, selection lines and light gizmos are not vehicle geometry.
            if (renderer.TryCast<MeshRenderer>() is { })
            {
                if (renderer.GetComponent<MeshFilter>()?.sharedMesh is not { } mesh) return;
                AddMesh(mesh, renderer.transform.localToWorldMatrix, renderer, owner, originals);
            }
            else if (renderer.TryCast<SkinnedMeshRenderer>() is { } skin)
            {
                var posed = new Mesh();
                try
                {
                    skin.BakeMesh(posed, true);
                    AddMesh(posed, Matrix4x4.TRS(skin.transform.position, skin.transform.rotation, Vector3.one), renderer, owner, originals);
                }
                finally { UnityEngine.Object.Destroy(posed); }
            }
        }

        void AddMesh(Mesh mesh, Matrix4x4 localToWorld, Renderer renderer, VehicleObject owner, Il2CppReferenceArray<VehicleMaterial>? originals)
        {
            RawMesh raw;
            try
            {
                if (!meshCache.TryGetValue(mesh.Pointer, out raw!)) meshCache[mesh.Pointer] = raw = ReadMesh(mesh);
            }
            catch (Exception ex) { throw new InvalidOperationException($"Could not read all geometry for {owner.name}/{renderer.name}. No OBJ was saved: {ex.Message}", ex); }
            if (raw.Faces.Length == 0) return;
            N.Matrix4x4 world = Matrix(localToWorld), output = world * worldToVehicle * N.Matrix4x4.CreateScale(-1, 1, 1);
            N.Matrix4x4 outputNormal = ObjExportGeometry.NormalMatrix(output), worldNormal = ObjExportGeometry.NormalMatrix(world);
            bool reverse = ObjExportGeometry.ReverseWinding(output);
            var points = raw.Points.Select(p => N.Vector3.Transform(p, output)).ToArray();
            var worldPoints = raw.Points.Select(p => N.Vector3.Transform(p, world)).ToArray();
            var normals = raw.Normals.Length == raw.Points.Length ? raw.Normals.Select(n => ObjExportGeometry.ApplyNormalMatrix(n, outputNormal)).ToArray() : null;
            var worldNormals = raw.Normals.Length == raw.Points.Length ? raw.Normals.Select(n => ObjExportGeometry.ApplyNormalMatrix(n, worldNormal)).ToArray() : Array.Empty<N.Vector3>();
            var indices = raw.Faces.Select(f => reverse ? f.Reverse().ToArray() : f.ToArray()).ToArray();
            var faceMaterials = new string[indices.Length];
            var nativeMaterials = renderer.sharedMaterials;
            var submeshMaterials = new Dictionary<int, string>();
            foreach (int s in raw.Submeshes.Distinct())
            {
                Material? material = originals != null && s < originals.Length ? originals[s]?.Material : null;
                material ??= s < nativeMaterials.Length ? nativeMaterials[s] : null;
                submeshMaterials.Add(s, MaterialName(material, 1, false));
            }
            for (int f = 0; f < indices.Length; f++) faceMaterials[f] = submeshMaterials[raw.Submeshes[f]];
            var corners = indices.Select(f => f.Select(i => new ObjCorner(i, raw.UV.Length == points.Length ? i : -1, normals != null ? i : -1)).ToArray()).ToArray();
            Add(new(owner.name + "_" + ((int)owner.VUID).ToString(Invariant) + "_" + Meshes.Count.ToString(Invariant), points, indices,
                raw.UV.Length == points.Length ? raw.UV : null, normals, corners, faceMaterials));
            receivers.Add(new(raw, worldPoints, worldNormals, renderer));
        }

        void Add(ObjMeshData mesh)
        {
            if ((vertices += mesh.Vertices.Length) > ObjMeshFormat.MaxVertices || (faces += mesh.Faces.Length) > ObjMeshFormat.MaxFaces || Meshes.Count >= ObjMeshFormat.MaxMeshes)
                throw new InvalidOperationException("The selected tank geometry exceeds the OBJ import/export limits. Export fewer parts in separate files.");
            Meshes.Add(mesh);
        }

        void ReadDecals()
        {
            var projected = new Dictionary<IntPtr, DecalProjector>();
            foreach (var part in parts.Values.Where(p => selected.Contains((int)p.VUID)))
            {
                foreach (var component in DesignEditor.Each(part.Components))
                    if (component?.TryCast<ProjectedDecal>()?.projector is { } projector) projected[projector.Pointer] = projector;
                foreach (var projector in part.GetComponentsInChildren<DecalProjector>(true))
                {
                    var owner = projector.GetComponentInParent<VehicleObject>();
                    if (owner == null || selected.Contains((int)owner.VUID)) projected[projector.Pointer] = projector;
                }
            }
            foreach (var projector in projected.Values)
            {
                if (projector.material == null) throw new InvalidOperationException("A selected decal has no material. No incomplete OBJ was saved.");
                var decalToWorld = ProjectorMatrix(projector);
                if (Math.Abs(decalToWorld.determinant) < 1e-10f) continue;
                var worldToDecal = decalToWorld.inverse;
                var vertices = new List<N.Vector3>(); var normals = new List<N.Vector3>(); var uv = new List<N.Vector2>(); var faces = new List<int[]>();
                foreach (var receiver in receivers)
                {
                    uint mask = unchecked((uint)projector.decalLayerMask);
                    if (mask != 0 && receiver.Renderer.renderingLayerMask != 0 && (mask & receiver.Renderer.renderingLayerMask) == 0) continue;
                    var local = receiver.World.Select(p => V(worldToDecal.MultiplyPoint3x4(U(p)))).ToArray();
                    if (Outside(local)) continue;
                    foreach (var face in receiver.Raw.Faces)
                        for (int t = 1; t < face.Length - 1; t++)
                        {
                            int[] tri = { face[0], face[t], face[t + 1] };
                            var surfaceNormal = N.Vector3.Cross(receiver.World[tri[1]] - receiver.World[tri[0]], receiver.World[tri[2]] - receiver.World[tri[0]]);
                            if (surfaceNormal.LengthSquared() < 1e-15f) continue;
                            surfaceNormal = receiver.WorldNormals.Length > 0 ? receiver.WorldNormals[tri[0]] : N.Vector3.Normalize(surfaceNormal);
                            float cos = Math.Clamp(N.Vector3.Dot(surfaceNormal, V(-projector.transform.forward)), -1, 1);
                            float angle = MathF.Acos(cos) * 180 / MathF.PI;
                            if (angle >= projector.endAngleFade && projector.endAngleFade < 179.99f) continue;
                            var clipped = ObjExportGeometry.ClipToProjector(tri.Select(i => new ObjExportGeometry.ProjectedVertex(local[i], receiver.World[i],
                                receiver.WorldNormals.Length > 0 ? receiver.WorldNormals[i] : surfaceNormal)).ToArray());
                            if (clipped.Length < 3) continue;
                            int start = vertices.Count;
                            foreach (var point in clipped)
                            {
                                var normal = point.Normal.LengthSquared() > 1e-14f ? N.Vector3.Normalize(point.Normal) : surfaceNormal;
                                vertices.Add(ObjExportGeometry.Reflect(N.Vector3.Transform(point.World + normal * 0.0002f, worldToVehicle)));
                                normals.Add(ObjExportGeometry.Reflect(ObjExportGeometry.ApplyNormalMatrix(normal, normalToVehicle)));
                                uv.Add(new((point.Local.X + 0.5f) * projector.uvScale.x + projector.uvBias.x,
                                    (point.Local.Z + 0.5f) * projector.uvScale.y + projector.uvBias.y));
                            }
                            // Receiver face winding and its world scale may already be mirrored.
                            var polygon = Enumerable.Range(start, clipped.Length).ToArray();
                            N.Vector3 faceNormal = N.Vector3.Cross(vertices[polygon[1]] - vertices[polygon[0]], vertices[polygon[2]] - vertices[polygon[0]]);
                            if (N.Vector3.Dot(faceNormal, normals[start]) < 0) Array.Reverse(polygon);
                            faces.Add(polygon);
                        }
                }
                if (faces.Count == 0) continue;
                string material = MaterialName(projector.material, projector.fadeFactor, true);
                var corners = faces.Select(f => f.Select(i => new ObjCorner(i, i, i)).ToArray()).ToArray();
                Add(new("Decal_" + DecalCount.ToString(Invariant), vertices.ToArray(), faces.ToArray(), uv.ToArray(), normals.ToArray(), corners,
                    Enumerable.Repeat(material, faces.Count).ToArray()));
                DecalCount++;
            }
        }

        static bool Outside(N.Vector3[] points)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float min = float.PositiveInfinity, max = float.NegativeInfinity;
                foreach (var p in points)
                {
                    float value = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
                    min = Math.Min(min, value); max = Math.Max(max, value);
                }
                if (min > 0.50001f || max < -0.50001f) return true;
            }
            return false;
        }

        string MaterialName(Material? material, float opacity, bool decal)
        {
            var key = (material?.Pointer ?? IntPtr.Zero, opacity, decal);
            if (materials.TryGetValue(key, out var existing)) return existing;
            string name = "material_" + materials.Count.ToString(Invariant);
            materials[key] = name;
            Color colour = Color.white;
            Texture? texture = null;
            string property = "";
            if (material != null)
            {
                foreach (string candidate in new[] { "_BaseColor", "_Color", "_BaseColour" })
                    if (material.HasColor(candidate)) { colour = material.GetColor(candidate); break; }
                // Only read advertised properties; probing the native multi-texture helper can log
                // shader-property warnings for custom materials and decals.
                foreach (string candidate in new[] { VehicleMaterial.BaseColourTextureReference, "_BaseColorMap", "_MainTex", "_BaseColourTexture", "_BaseColorTexture" })
                    if (material.HasTexture(candidate) && material.GetTexture(candidate) is { } found) { texture = found; property = candidate; break; }
            }
            MaterialText.Append("\nnewmtl ").Append(name).Append("\nKa 0 0 0\nKd ").Append(F(colour.r)).Append(' ').Append(F(colour.g)).Append(' ').Append(F(colour.b))
                .Append("\nKs 0 0 0\nNs 1\nd ").Append(F(Math.Clamp(colour.a * opacity, 0, 1))).Append("\nillum 1\n");
            if (texture != null)
            {
                var textureKey = (texture.Pointer, false);
                if (!textureNames.TryGetValue(textureKey, out string? file))
                {
                    file = "texture_" + textureNames.Count.ToString(Invariant) + ".png";
                    textureNames[textureKey] = file;
                    Textures[file] = ReadTexture(texture, false);
                }
                Vector2 scale = Vector2.one, offset = Vector2.zero;
                if (material != null && property.Length > 0) { scale = material.GetTextureScale(property); offset = material.GetTextureOffset(property); }
                string mapping = " -s " + F(scale.x) + " " + F(scale.y) + " 1 -o " + F(offset.x) + " " + F(offset.y) + " 0 " + assetName + "/" + file;
                MaterialText.Append("map_Kd").Append(mapping).Append('\n');
                if (decal)
                {
                    var alphaKey = (texture.Pointer, true);
                    if (!textureNames.TryGetValue(alphaKey, out string? alpha))
                    {
                        alpha = "opacity_" + textureNames.Count.ToString(Invariant) + ".png";
                        textureNames[alphaKey] = alpha;
                        Textures[alpha] = ReadTexture(texture, true);
                    }
                    MaterialText.Append("map_d -imfchan l -s ").Append(F(scale.x)).Append(' ').Append(F(scale.y)).Append(" 1 -o ")
                        .Append(F(offset.x)).Append(' ').Append(F(offset.y)).Append(" 0 ").Append(assetName).Append('/').Append(alpha).Append('\n');
                }
            }
            return name;
        }
    }

    static RawMesh ReadMesh(Mesh mesh)
    {
        N.Vector3[] points, normals; N.Vector2[] uv;
        if (mesh.isReadable)
        {
            points = mesh.vertices.Select(V).ToArray(); normals = mesh.normals.Select(V).ToArray(); uv = mesh.uv.Select(v => new N.Vector2(v.x, v.y)).ToArray();
        }
        else
        {
            var streams = new Dictionary<int, byte[]>();
            N.Vector3[] Attribute(VertexAttribute attribute, bool required)
            {
                if (!mesh.HasVertexAttribute(attribute))
                {
                    if (required) throw new InvalidOperationException("The mesh has no vertex positions.");
                    return Array.Empty<N.Vector3>();
                }
                int stream = mesh.GetVertexAttributeStream(attribute), format = (int)mesh.GetVertexAttributeFormat(attribute);
                int stride = mesh.GetVertexBufferStride(stream), offset = mesh.GetVertexAttributeOffset(attribute), dimension = mesh.GetVertexAttributeDimension(attribute);
                if (!streams.TryGetValue(stream, out var bytes))
                {
                    var buffer = mesh.GetVertexBuffer(stream);
                    try { streams[stream] = bytes = ReadBuffer(buffer); }
                    finally { buffer?.Dispose(); }
                }
                int width = ObjExportGeometry.AttributeBytes(format);
                if (stride <= 0 || dimension < 1 || dimension > 4 || offset < 0 || offset + dimension * width > stride || (long)mesh.vertexCount * stride > bytes.Length)
                    throw new InvalidOperationException("The GPU mesh has an invalid vertex layout.");
                var values = new N.Vector3[mesh.vertexCount];
                for (int i = 0; i < values.Length; i++)
                {
                    int start = checked(i * stride + offset);
                    float x = ObjExportGeometry.ReadAttribute(bytes, start, format);
                    float y = dimension > 1 ? ObjExportGeometry.ReadAttribute(bytes, start + width, format) : 0;
                    float z = dimension > 2 ? ObjExportGeometry.ReadAttribute(bytes, start + 2 * width, format) : 0;
                    values[i] = new(x, y, z);
                }
                return values;
            }
            points = Attribute(VertexAttribute.Position, true); normals = Attribute(VertexAttribute.Normal, false);
            uv = Attribute(VertexAttribute.TexCoord0, false).Select(v => new N.Vector2(v.X, v.Y)).ToArray();
        }
        if (points.Length == 0 || points.Length > ObjMeshFormat.MaxVertices) throw new InvalidOperationException("The mesh has no vertices or exceeds the export limit.");
        var faces = new List<int[]>(); var submeshes = new List<int>();
        byte[]? indexBytes = null;
        if (!mesh.isReadable)
        {
            var buffer = mesh.GetIndexBuffer();
            try { indexBytes = ReadBuffer(buffer); }
            finally { buffer?.Dispose(); }
        }
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            MeshTopology topology = mesh.GetTopology(s);
            if (topology is MeshTopology.Lines or MeshTopology.LineStrip or MeshTopology.Points) continue;
            int[] indices;
            if (mesh.isReadable) indices = mesh.GetIndices(s, true).ToArray();
            else
            {
                var desc = mesh.GetSubMesh(s);
                int width = mesh.indexFormat == IndexFormat.UInt32 ? 4 : 2;
                if (desc.indexStart < 0 || desc.indexCount < 0 || ((long)desc.indexStart + desc.indexCount) * width > indexBytes!.Length)
                    throw new InvalidOperationException("The GPU mesh has an invalid index range.");
                indices = new int[desc.indexCount];
                for (int i = 0; i < indices.Length; i++)
                {
                    int offset = checked((desc.indexStart + i) * width);
                    indices[i] = checked((width == 4 ? (int)BitConverter.ToUInt32(indexBytes, offset) : BitConverter.ToUInt16(indexBytes, offset)) + desc.baseVertex);
                }
            }
            int count = topology == MeshTopology.Quads ? 4 : topology == MeshTopology.Triangles ? 3 : 0;
            if (count == 0 || indices.Length % count != 0) throw new InvalidOperationException("The mesh uses unsupported or incomplete polygon topology.");
            for (int i = 0; i < indices.Length; i += count)
            {
                var face = indices.Skip(i).Take(count).ToArray();
                if (face.Any(v => v < 0 || v >= points.Length)) throw new InvalidOperationException("The mesh has out-of-range face indices.");
                if (face.Distinct().Count() != count || !ObjExportGeometry.HasSurface(points, face)) continue; // invisible padding, including separate UV vertices at the same point
                faces.Add(face); submeshes.Add(s);
            }
        }
        if (normals.Length != points.Length)
        {
            normals = new N.Vector3[points.Length];
            foreach (var face in faces)
                for (int i = 1; i < face.Length - 1; i++)
                {
                    var normal = N.Vector3.Cross(points[face[i]] - points[face[0]], points[face[i + 1]] - points[face[0]]);
                    normals[face[0]] += normal; normals[face[i]] += normal; normals[face[i + 1]] += normal;
                }
            for (int i = 0; i < normals.Length; i++)
                if (normals[i].LengthSquared() > 1e-15f) normals[i] = N.Vector3.Normalize(normals[i]);
        }
        return new(points, normals, uv, faces.ToArray(), submeshes.ToArray());
    }

    static byte[] ReadBuffer(GraphicsBuffer? buffer)
    {
        if (buffer == null || !buffer.IsValid()) throw new InvalidOperationException("The mesh's graphics buffer is unavailable.");
        long length = (long)buffer.count * buffer.stride;
        if (length <= 0 || length > 256 * 1024 * 1024) throw new InvalidOperationException("The graphics buffer is empty or too large.");
        var bytes = new Il2CppStructArray<byte>(length);
        buffer.InternalGetData(bytes.Cast<Il2CppSystem.Array>(), 0, 0, checked((int)length), 1);
        return bytes.ToArray();
    }

    static byte[] ReadTexture(Texture texture, bool alpha)
    {
        if (texture.width <= 0 || texture.height <= 0 || (long)texture.width * texture.height > 64 * 1024 * 1024)
            throw new InvalidOperationException("A material texture is too large to export safely.");
        Texture2D? copy = null; RenderTexture? target = null;
        var previous = RenderTexture.active;
        try
        {
            var readable = texture.TryCast<Texture2D>();
            // PNG encoding takes plain pixels only: anything else (compressed, HDR) is drawn into a plain copy first.
            if (readable == null || !readable.isReadable || readable.format is not (TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.RGB24))
            {
                target = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(texture, target); RenderTexture.active = target;
                copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); copy.Apply();
                readable = copy;
            }
            if (alpha)
            {
                var pixels = readable.GetPixels32();
                for (int i = 0; i < pixels.Length; i++) { byte a = pixels[i].a; pixels[i] = new Color32(a, a, a, 255); }
                if (copy == null) copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                copy.SetPixels32(pixels); copy.Apply(); readable = copy;
            }
            return ImageConversion.EncodeToPNG(readable).ToArray();
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (copy != null) UnityEngine.Object.Destroy(copy);
        }
    }

    /// Mirrors HDRP's size/pivot resolution, including a projector inherited from a mirrored part.
    static Matrix4x4 ProjectorMatrix(DecalProjector projector)
    {
        Vector3 scale = projector.effectiveScale, sizeScale = scale, offsetScale = scale;
        if (scale.z < 0) { sizeScale.y *= -1; offsetScale.y *= -1; offsetScale.z *= -1; }
        if ((sizeScale.x < 0) ^ (sizeScale.y < 0) ^ (sizeScale.z < 0)) sizeScale.z *= -1;
        var size = new Vector3(projector.size.x * sizeScale.x, projector.size.z * sizeScale.z, projector.size.y * sizeScale.y);
        var offset = new Vector3(projector.pivot.x * offsetScale.x, -projector.pivot.z * offsetScale.z, projector.pivot.y * offsetScale.y);
        var rotation = projector.transform.rotation * Quaternion.Euler(scale.z >= 0 ? -90 : 90, 0, 0);
        return Matrix4x4.TRS(projector.transform.position, rotation, Vector3.one) * Matrix4x4.Translate(offset) * Matrix4x4.Scale(size);
    }

    static N.Vector3 V(Vector3 p) => new(p.x, p.y, p.z);
    static Vector3 U(N.Vector3 p) => new(p.X, p.Y, p.Z);
    static N.Matrix4x4 Matrix(Matrix4x4 m) => new(m.m00, m.m10, m.m20, m.m30, m.m01, m.m11, m.m21, m.m31,
        m.m02, m.m12, m.m22, m.m32, m.m03, m.m13, m.m23, m.m33);
    static string F(float number) => number.ToString("R", Invariant);
}
