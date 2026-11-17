using System.Numerics;
using System.Text.Json;
using Engine.Rendering.Assets;
using Shared.Contracts.Content;
using Shared.Contracts.Rendering;
using Shared.Contracts.World;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal sealed record VegetationPart(string MaterialId, AssetHandle Mesh);
internal sealed record VegetationLod(float MaxDistance, VegetationPart[] Parts);
internal sealed record VegetationAsset(int Index, string Id, string Kind, string Species, float Height, VegetationLod[] Lods)
{
    public float MaxDistance => Lods[^1].MaxDistance;

    /// <summary>The level of detail for a viewer at the given distance, or -1 beyond the last.</summary>
    public int LodAt(float distance)
    {
        for (int i = 0; i < Lods.Length; i++) if (distance <= Lods[i].MaxDistance) return i;
        return -1;
    }
}

/// <summary>
/// Blender-authored vegetation (tools/storm-chase-models/build_vegetation.py): trees,
/// shrubs, ground-cover patches and boulders, each with explicit levels of detail.
/// </summary>
internal sealed class StormChaseVegetationLibrary
{
    private readonly List<VegetationAsset> assets = [];
    private readonly Dictionary<string, VegetationAsset[]> bySpecies = new(StringComparer.Ordinal);

    public IReadOnlyList<VegetationAsset> Assets => assets;
    public string ContentDirectory { get; }
    public int VertexCount { get; private set; }
    public int TriangleCount { get; private set; }

    public StormChaseVegetationLibrary(MutableRenderAssetCatalog catalog, string? contentRoot = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ContentDirectory = Locate(contentRoot);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContentDirectory, "storm-chase-vegetation.json")));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unsupported Blender vegetation schema.");
        foreach (var texture in root.GetProperty("textures").EnumerateObject())
            RegisterTexture(catalog, texture.Name, Path.Combine(ContentDirectory, texture.Value.GetString()!));
        foreach (var material in root.GetProperty("materials").EnumerateArray()) RegisterMaterial(catalog, material);
        foreach (var node in root.GetProperty("assets").EnumerateArray())
        {
            string id = node.GetProperty("id").GetString()!;
            var lods = new List<VegetationLod>();
            int level = 0;
            foreach (var lod in node.GetProperty("lods").EnumerateArray())
            {
                var parts = new List<VegetationPart>();
                foreach (var part in lod.GetProperty("parts").EnumerateArray())
                {
                    string material = part.GetProperty("material").GetString()!;
                    var handle = new AssetHandle("mesh", $"chase/veg/{id}/lod{level}/{material}");
                    catalog.UpsertMesh(ReadMesh(handle, part));
                    parts.Add(new("chase/veg/" + material, handle));
                }
                lods.Add(new(lod.GetProperty("maxDistanceMeters").GetSingle(), parts.ToArray()));
                level++;
            }
            float height = node.TryGetProperty("heightMeters", out var h) ? h.GetSingle() : 1;
            var asset = new VegetationAsset(assets.Count, id, node.GetProperty("kind").GetString()!,
                node.GetProperty("species").GetString()!, height, lods.ToArray());
            assets.Add(asset);
        }
        foreach (var group in assets.GroupBy(a => a.Species, StringComparer.Ordinal)) bySpecies[group.Key] = group.ToArray();
        foreach (string required in new[] { "cottonwood", "bur-oak", "red-cedar", "silver-maple", "plum-shrub", "prairie", "flowers", "lawn", "corn", "boulder" })
            if (!bySpecies.ContainsKey(required)) throw new InvalidDataException("Required Blender vegetation absent: " + required);
    }

    public VegetationAsset[] Species(string species) =>
        bySpecies.TryGetValue(species, out var variants) ? variants : throw new ArgumentException("Unknown vegetation species: " + species, nameof(species));

    private RenderMeshData ReadMesh(AssetHandle handle, JsonElement part)
    {
        var vertices = new List<RenderMeshVertex>();
        foreach (var v in part.GetProperty("vertices").EnumerateArray())
        {
            var p = new Vector3f(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
            var n = new Vector3(v[3].GetSingle(), v[4].GetSingle(), v[5].GetSingle());
            if (!float.IsFinite(p.X + p.Y + p.Z) || !float.IsFinite(n.LengthSquared()) || n.LengthSquared() < .5f)
                throw new InvalidDataException("Invalid exported Blender vegetation vertex: " + handle);
            n = Vector3.Normalize(n);
            var tangent = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) > .95f ? Vector3.UnitZ : Vector3.UnitY, n));
            vertices.Add(new(p, new(v[8].GetSingle(), v[9].GetSingle(), v[10].GetSingle(), v[11].GetSingle()),
                v[6].GetSingle(), v[7].GetSingle(), new(n.X, n.Y, n.Z), new(tangent.X, tangent.Y, tangent.Z)));
        }
        var indices = part.GetProperty("indices").EnumerateArray().Select(i => i.GetUInt32()).ToArray();
        if (indices.Length == 0 || indices.Length % 3 != 0 || indices.Any(i => i >= vertices.Count))
            throw new InvalidDataException("Invalid exported Blender vegetation index range: " + handle);
        VertexCount += vertices.Count; TriangleCount += indices.Length / 3;
        return new RenderMeshData { Handle = handle, Vertices = vertices, WideIndices = indices };
    }

    private static void RegisterTexture(MutableRenderAssetCatalog catalog, string name, string path)
    {
        using var image = Image.Load<Rgba32>(path);
        byte[] bytes = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(bytes);
        // Alpha-coverage preservation keeps distant leaf and grass cards from thinning out.
        var mip = TextureMipChainGenerator.Generate((uint)image.Width, (uint)image.Height, 1, bytes, TextureMipSemantic.Color, true);
        catalog.UpsertTexture(new() { Handle = new("texture", "chase/veg/" + name), Width = (uint)image.Width, Height = (uint)image.Height,
            MipLevels = mip.MipLevels, ColorSpace = "srgb", PixelDataRgba8 = mip.PixelDataRgba8 });
    }

    private static void RegisterMaterial(MutableRenderAssetCatalog catalog, JsonElement node)
    {
        string id = node.GetProperty("id").GetString()!;
        string texture = node.GetProperty("texture").GetString()!;
        bool foliage = node.GetProperty("shader").GetString() == "foliage";
        // Names containing '/' refer to textures already registered by the world (e.g. chase/weathered-rock).
        var albedo = new AssetHandle("texture", texture.Contains('/') ? texture : "chase/veg/" + texture);
        catalog.UpsertMaterial(new()
        {
            Handle = new("material", "chase/veg/" + id),
            ShaderProgramId = foliage ? BuiltInShaderLibrary.FoliageLit.Id : BuiltInShaderLibrary.PbrLit.Id,
            LightingProfile = RenderMaterialLightingProfile.MetallicRoughness, Tint = new(1, 1, 1, 1), AlbedoTexture = albedo,
            RoughnessStrength = node.GetProperty("roughness").GetSingle(),
            CullMode = node.GetProperty("cull").GetString() == "none" ? RenderCullMode.None : RenderCullMode.Back,
            OpacityMode = foliage ? RenderOpacityMode.Masked : RenderOpacityMode.Opaque,
            AlphaCutoff = foliage ? node.GetProperty("alphaCutoff").GetSingle() : .5f,
            FeatureFlags = RenderMaterialFeatureFlags.WetnessResponse | (foliage ? RenderMaterialFeatureFlags.FoliageWind : 0),
        });
    }

    private static string Locate(string? contentRoot)
    {
        if (contentRoot is not null)
        {
            string direct = Path.Combine(contentRoot, "Assets", "StormChase", "Vegetation");
            if (File.Exists(Path.Combine(direct, "storm-chase-vegetation.json"))) return direct;
        }
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory, "Assets", "StormChase", "Vegetation");
            if (File.Exists(Path.Combine(candidate, "storm-chase-vegetation.json"))) return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException("Blender vegetation is missing; run tools/storm-chase-models/build_vegetation.py.", "storm-chase-vegetation.json");
    }
}
