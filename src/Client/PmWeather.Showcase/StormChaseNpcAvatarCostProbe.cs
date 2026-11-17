using System.Diagnostics;
using System.Text.Json;
using Engine.Avatar;
using Engine.Avatar.Generation;
using Engine.Rendering.Assets;
using Tools.Avatar.UMA;

/// <summary>
/// Measures what one genetically unique UMA person costs: build time, and the texture/mesh bytes each new
/// person adds to the render catalog. Builds several people with different DNA sliders and pigmentation.
/// </summary>
internal static class StormChaseNpcAvatarCostProbe
{
    public static int Run(string output, string[] args)
    {
        Directory.CreateDirectory(output);
        string catalogPath = Showcase.OptionalGameValue(args, "--avatar-catalog") ?? @"C:\Users\reviv\youradventure\Old Engine\artifacts\avatar\uma\uma-catalog.json";
        var clock = Stopwatch.StartNew();
        var factory = args.Contains("--deferred-colours")
            ? new UmaAvatarFactory(catalogPath, null, [UmaAvatarSharedColorChannels.Skin, UmaAvatarSharedColorChannels.Hair, UmaAvatarSharedColorChannels.Eyes])
            : new UmaAvatarFactory(catalogPath);
        double factoryMs = clock.Elapsed.TotalMilliseconds;
        var catalog = new MutableRenderAssetCatalog(new BuiltInRenderAssetCatalog());
        var rows = new List<object>();
        var seen = new HashSet<string>();
        foreach (var (race, wardrobe) in new[]
        {
            ("o3n Stunner Jane", new[] { "st_female_shirt_05_Recipe", "st_female_pants_01_classic_Recipe", "st_female_shoes_flat_01_Recipe", "st_female_long_hair_Recipe" }),
            ("o3n Stunner John", new[] { "st_male_shirt_01_Recipe", "st_male_pants_01_jeans_Recipe", "st_shoes_07_Recipe", "st_short_hair_Recipe" }),
        })
        {
            var archetype = factory.ResolveArchetypeId(race);
            var raceEntry = factory.Content.Source.Races.First(r => r.RaceName == race);
            var dnaNames = factory.Content.Source.DnaConverters.Where(c => raceEntry.DnaConverters.Any(r => r.Guid == c.Source.Guid))
                .SelectMany(c => c.DnaNames).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
            Console.WriteLine($"{race}: {dnaNames.Length} DNA sliders: {string.Join(", ", dnaNames.Take(60))}");
            for (int person = 0; person < 3; person++)
            {
                var random = new Random(person * 31 + race.Length);
                var dna = dnaNames.ToDictionary(n => new AvatarParameterId("uma.dna." + n), _ => (float)(.35 + random.NextDouble() * .3));
                // --same-colours: vary only the 60 DNA shape sliders, to isolate shape cost from colour composites.
                bool sameColours = args.Contains("--same-colours");
                if (!sameColours)
                {
                    dna[AvatarParameterIds.SkinPigmentation] = (float)random.NextDouble();
                    dna[AvatarParameterIds.HairPigmentation] = (float)random.NextDouble();
                    dna[AvatarParameterIds.EyePigmentation] = (float)random.NextDouble();
                }
                else dna.Remove(new AvatarParameterId("uma.dna.skinTone"));
                dna[AvatarParameterIds.Height] = (float)random.NextDouble();
                dna[AvatarParameterIds.Build] = (float)random.NextDouble();
                var recipe = new AvatarAppearanceRecipe(archetype, ageYears: 25 + person * 15,
                    wardrobeItems: wardrobe.Select(factory.ResolveWardrobeItemId), dnaParameters: dna);
                clock.Restart();
                using var instance = factory.CreateInstance($"cost-{race}-{person}", catalog, recipe, gpuSkinningEnabled: true);
                double ms = clock.Elapsed.TotalMilliseconds;
                var (textures, meshes) = NewBytes(catalog, instance.CreateRenderItems($"cost/{race}/{person}"), seen);
                var tints = instance.CreateRenderItems("tints").Select(i => i.Material).OfType<Shared.Contracts.Content.AssetHandle>().Distinct()
                    .Select(h => catalog.TryGetMaterial(h, out var m) && m is not null ? m.Tint : default).Where(t => t.R < .999f || t.G < .999f || t.B < .999f)
                    .Select(t => $"{t.R:0.00}/{t.G:0.00}/{t.B:0.00}").Distinct().ToArray();
                Console.WriteLine($"  person {person}: {ms:0} ms, +{textures / 1048576.0:0.0} MiB textures, +{meshes / 1048576.0:0.0} MiB meshes, {instance.Parts.Count} parts; tinted materials: {string.Join(" ", tints)}");
                rows.Add(new { race, person, ms, TextureMiB = textures / 1048576.0, MeshMiB = meshes / 1048576.0, Parts = instance.Parts.Count });
                GC.KeepAlive(instance);
            }
        }
        File.WriteAllText(Path.Combine(output, "npc-avatar-cost.json"), JsonSerializer.Serialize(new { factoryMs, rows }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>Bytes of meshes and textures this person references that no earlier person did.</summary>
    private static (long Textures, long Meshes) NewBytes(MutableRenderAssetCatalog catalog, IEnumerable<Shared.Contracts.Rendering.RenderItem> items, HashSet<string> seen)
    {
        long textures = 0, meshes = 0;
        foreach (var item in items)
        {
            if (seen.Add("m:" + item.Mesh) && catalog.TryGetMesh(item.Mesh, out var mesh) && mesh is not null)
                meshes += (long)mesh.Vertices.Count * 64 + mesh.Indices.Count * 2L + mesh.WideIndices.Count * 4L;
            if (item.Material is not { } handle || !catalog.TryGetMaterial(handle, out var material) || material is null) continue;
            foreach (var texture in new[] { material.AlbedoTexture, material.NormalTexture, material.SurfaceTexture, material.EmissiveTexture })
                if (texture is { } t && seen.Add("t:" + t) && catalog.TryRestoreTexturePayload(t, out var data) && data is not null)
                {
                    textures += data.PixelDataRgba8.Length + data.PixelDataCompressed.Length;
                    if (seen.Count > 40) Console.WriteLine($"      new texture {t.Id} {data.Width}x{data.Height} for part material {handle.Id}");
                }
        }
        return (textures, meshes);
    }
}
