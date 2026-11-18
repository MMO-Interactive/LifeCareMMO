using System.Numerics;
using System.Text.Json;

namespace LifeCare.Construction;

/// <summary>
/// The rural module pack's meshes, read straight from its GLBs (assets/modules/rural/glb). Each module becomes one mesh
/// whose vertex colours carry its glTF materials' base colours, so a whole module draws as one item with one material.
/// glTF axes are used as they are: the repo's Blender exports and the engine share X right, Y up, Z forward.
/// </summary>
public sealed class ModuleMeshLibrary
{
    private readonly Dictionary<string, MeshBuffer> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> vertexMaterials = new(StringComparer.Ordinal);
    public string Directory { get; }
    public IReadOnlyCollection<string> ModuleIds => modules.Keys;
    public int Count => modules.Count;

    private ModuleMeshLibrary(string directory) => Directory = directory;

    /// <summary>Loads every module listed in the pack's manifest.json.</summary>
    public static ModuleMeshLibrary Load(string packDirectory)
    {
        var library = new ModuleMeshLibrary(Path.GetFullPath(packDirectory));
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(packDirectory, "manifest.json")));
        foreach (var module in manifest.RootElement.GetProperty("modules").EnumerateArray())
        {
            string id = module.GetProperty("id").GetString()!;
            library.modules[id] = ReadGlb(Path.Combine(packDirectory, module.GetProperty("file").GetString()!), out var materials);
            library.vertexMaterials[id] = materials;
        }
        return library;
    }

    /// <summary>Finds the pack beside or above <paramref name="start"/>: a packaged Assets/Modules/Rural, or the repo's assets/modules/rural.</summary>
    public static string? Locate(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            foreach (var candidate in new[] { Path.Combine(directory.FullName, "Assets", "Modules", "Rural"), Path.Combine(directory.FullName, "assets", "modules", "rural") })
                if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
        return null;
    }

    public bool Has(string moduleId) => modules.ContainsKey(moduleId);
    public MeshBuffer? Module(string moduleId) => modules.GetValueOrDefault(moduleId);

    /// <summary>One mesh for a piece: its modules turned and offset into the piece's frame. Unknown modules are skipped.</summary>
    public MeshBuffer? Compose(IReadOnlyList<ModulePart> parts)
    {
        var mesh = new MeshBuffer();
        foreach (var part in parts)
        {
            if (!modules.TryGetValue(part.ModuleId, out var source)) continue;
            var materials = vertexMaterials[part.ModuleId];
            var inside = ModuleCatalog.PaintColor(part.InsidePaint); var outside = ModuleCatalog.PaintColor(part.OutsidePaint);
            var floor = ModuleCatalog.PaintColor(part.FloorPaint);
            int baseIndex = mesh.VertexCount;
            for (int i = 0; i < source.VertexCount; i++)
            {
                var p = source.Positions[i]; var n = source.Normals[i];
                var (px, pz) = LotGeometry.Turn(p.X, p.Z, part.QuarterTurns);
                var (nx, nz) = LotGeometry.Turn(n.X, n.Z, part.QuarterTurns);
                mesh.Positions.Add(new(px + part.OffsetX, p.Y + part.OffsetY, pz + part.OffsetZ));
                mesh.Normals.Add(new(nx, n.Y, nz));
                mesh.Uvs.Add(source.Uvs[i]);
                var color = source.Colors[i];
                if (floor is { } f && ModuleCatalog.PaintableFloor.Contains(materials[i])) color = new(f.R, f.G, f.B, 1);
                else if ((p.Z >= 0 ? outside : inside) is { } paint && ModuleCatalog.PaintableWall.Contains(materials[i])) color = new(paint.R, paint.G, paint.B, 1);
                mesh.Colors.Add(color);
            }
            bool clipped = !float.IsNegativeInfinity(part.ClipFrom) || !float.IsPositiveInfinity(part.ClipTo);
            for (int t = 0; t + 2 < source.Indices.Count; t += 3)
            {
                int a = source.Indices[t], b = source.Indices[t + 1], c = source.Indices[t + 2];
                if (clipped)
                {
                    // A jamb or lintel piece keeps the triangles whose centre lies on its stretch of the wall.
                    float x = (source.Positions[a].X + source.Positions[b].X + source.Positions[c].X) / 3;
                    if (x < part.ClipFrom || x > part.ClipTo) continue;
                }
                mesh.Indices.Add(baseIndex + a); mesh.Indices.Add(baseIndex + b); mesh.Indices.Add(baseIndex + c);
            }
        }
        return mesh.VertexCount == 0 ? null : mesh;
    }

    /// <summary>Reads a GLB's triangle primitives into one mesh, colouring each primitive with its material's base colour.</summary>
    public static MeshBuffer ReadGlb(string path) => ReadGlb(path, out _);

    /// <summary>Also returns each vertex's material (the generator's palette name, without its LC_ prefix).</summary>
    public static MeshBuffer ReadGlb(string path, out string[] vertexMaterials)
    {
        var names = new List<string>();
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != 0x46546C67) throw new InvalidDataException("Not a GLB file: " + path);
        int jsonLength = BitConverter.ToInt32(bytes, 12);
        using var json = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        int binStart = 20 + jsonLength + 8;
        var root = json.RootElement;
        var accessors = root.GetProperty("accessors"); var views = root.GetProperty("bufferViews");
        var materials = root.TryGetProperty("materials", out var m) ? m : default;
        var mesh = new MeshBuffer();

        (int Offset, int Stride, int Count, int Components, int Type) Accessor(int index)
        {
            var a = accessors[index]; var view = views[a.GetProperty("bufferView").GetInt32()];
            int components = a.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, var t => throw new InvalidDataException("Unsupported accessor " + t) };
            int type = a.GetProperty("componentType").GetInt32();
            int size = type switch { 5126 or 5125 => 4, 5123 => 2, 5121 => 1, _ => throw new InvalidDataException("Unsupported component type " + type) };
            int offset = binStart + (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            int stride = view.TryGetProperty("byteStride", out var st) ? st.GetInt32() : size * components;
            return (offset, stride, a.GetProperty("count").GetInt32(), components, type);
        }
        float F(int at) => BitConverter.ToSingle(bytes, at);
        uint Index(int at, int type) => type switch { 5125 => BitConverter.ToUInt32(bytes, at), 5123 => BitConverter.ToUInt16(bytes, at), _ => bytes[at] };

        foreach (var gltfMesh in root.GetProperty("meshes").EnumerateArray())
            foreach (var primitive in gltfMesh.GetProperty("primitives").EnumerateArray())
            {
                if (primitive.TryGetProperty("mode", out var mode) && mode.GetInt32() != 4) continue;
                var attributes = primitive.GetProperty("attributes");
                var pos = Accessor(attributes.GetProperty("POSITION").GetInt32());
                (int Offset, int Stride, int Count, int Components, int Type)? nor = attributes.TryGetProperty("NORMAL", out var na) ? Accessor(na.GetInt32()) : null;
                (int Offset, int Stride, int Count, int Components, int Type)? uv = attributes.TryGetProperty("TEXCOORD_0", out var ua) ? Accessor(ua.GetInt32()) : null;
                var color = new Vector4(1, 1, 1, 1); string name = "";
                if (primitive.TryGetProperty("material", out var mi) && materials.ValueKind == JsonValueKind.Array)
                {
                    var material = materials[mi.GetInt32()];
                    name = material.TryGetProperty("name", out var n) ? n.GetString()!.Replace("LC_", "", StringComparison.Ordinal) : "";
                    if (material.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.TryGetProperty("baseColorFactor", out var bc))
                        color = new(bc[0].GetSingle(), bc[1].GetSingle(), bc[2].GetSingle(), 1);
                }
                int baseIndex = mesh.VertexCount;
                for (int i = 0; i < pos.Count; i++)
                {
                    int p = pos.Offset + i * pos.Stride;
                    mesh.Positions.Add(new(F(p), F(p + 4), F(p + 8)));
                    mesh.Normals.Add(nor is { } n ? new(F(n.Offset + i * n.Stride), F(n.Offset + i * n.Stride + 4), F(n.Offset + i * n.Stride + 8)) : Vector3.UnitY);
                    mesh.Uvs.Add(uv is { } t ? new(F(t.Offset + i * t.Stride), F(t.Offset + i * t.Stride + 4)) : Vector2.Zero);
                    mesh.Colors.Add(color); names.Add(name);
                }
                if (primitive.TryGetProperty("indices", out var ix))
                {
                    var idx = Accessor(ix.GetInt32());
                    for (int i = 0; i < idx.Count; i++) mesh.Indices.Add(baseIndex + (int)Index(idx.Offset + i * idx.Stride, idx.Type));
                }
                else for (int i = 0; i < pos.Count; i++) mesh.Indices.Add(baseIndex + i);
            }
        vertexMaterials = names.ToArray();
        return mesh;
    }
}
